using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Promotions;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Application.LiveCrash;

public enum LiveRoundStatus { BETTING, FLYING, CRASHED }
public enum LiveBetStatus { Pending, Active, CashedOut, Lost, Cancelled }

/// <summary>
/// One live crash game: shared rounds that every connected player watches together.
///   BETTING (countdown) → FLYING (multiplier e^(rate·t) climbs) → CRASHED (result shown) → BETTING …
/// Players bet during BETTING, then cash out during FLYING — by hand, or automatically at a target — and
/// are paid stake × multiplier. Anyone still in when the multiplier reaches the crash point loses the stake.
///
/// The crash point comes from this game's own RNG instance (IGameRngService, Target model) and is drawn
/// before betting opens; only its hash and nonce are public until the crash (provable fairness).
/// Money moves through WalletService (ledger + SdGaming): debit on bet, credit on cash-out, rollback on cancel.
///
/// Events go to this game's SignalR group only (see LiveCrashHub):
///   "Round"     public round state on every phase change
///   "Tick"      { t, m } while flying
///   "BetPlaced" / "CashedOut"  public feed of other players' bets
///   "MyBet"     to the owner only, on every change to one of their bets
/// Single-node: state lives in this process.
/// </summary>
public sealed class LiveCrashEngine(
    GameDefinition game,
    IGameRegistry games,
    IGameRngService rng,
    IServiceScopeFactory scopes,
    IHubContext<LiveCrashHub> hub,
    PromoEngine promos,
    ILogger logger)
{
    private static readonly long RoundEpoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private readonly LiveCrashSettings _live = game.Live ?? new LiveCrashSettings();
    private readonly object _lock = new();
    private readonly List<LiveBet> _bets = [];
    private readonly LinkedList<object> _history = new();
    private LiveRound _round = new() { Status = LiveRoundStatus.CRASHED };

    /// <summary>
    /// The current definition from the back office (limits, RNG). Round timing (Live) is read once at startup, and
    /// enabling / disabling a live crash game takes a restart — see LiveCrashRegistry.
    /// </summary>
    public GameDefinition Game =>
        games.All.FirstOrDefault(g => string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase)) ?? game;
    public LiveCrashSettings Settings => _live;
    public string Group => GroupFor(Game.GameId);
    public static string GroupFor(string gameId) => $"live:{gameId}";
    public static string PlayerGroup(string gameId, string playerId) => $"live:{gameId}:p:{playerId}";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task RunAsync(CancellationToken ct)
    {
        lock (_lock) StartBetting(Now());
        await hub.Clients.Group(Group).SendAsync("Round", PublicRound(), ct);
        logger.LogInformation("Live crash engine started: game={GameId}", Game.GameId);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_live.TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await StepAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Live crash tick failed: game={GameId}", Game.GameId); }
        }
    }

    private async Task StepAsync()
    {
        var now = Now();
        object? round = null;
        decimal? tick = null;
        List<(LiveBet Bet, decimal At)> autoCashOuts = [];
        List<LiveBet> losers = [];
        (long RoundId, decimal CrashPoint, List<LiveBet> Bets)? crashed = null;

        lock (_lock)
        {
            switch (_round.Status)
            {
                case LiveRoundStatus.BETTING when now >= _round.PhaseEndsAt:
                    _round.Status    = LiveRoundStatus.FLYING;
                    _round.StartedAt = now;
                    _bets.RemoveAll(b => b.Status == LiveBetStatus.Pending && b.RoundId != _round.Id);
                    round = PublicRound();
                    break;

                case LiveRoundStatus.FLYING:
                    var m = MultiplierAt(now);
                    // Auto cash-outs whose target was reached before (or at) the crash point win at their target.
                    var reached = Math.Min(m, _round.CrashPoint);
                    foreach (var b in _bets.Where(b => b.Status == LiveBetStatus.Active && b.AutoCashOut is { } a && a <= reached))
                    {
                        b.Status      = LiveBetStatus.CashedOut;
                        b.CashedOutAt = b.AutoCashOut;
                        autoCashOuts.Add((b, b.AutoCashOut!.Value));
                    }

                    if (m >= _round.CrashPoint)
                    {
                        _round.Status      = LiveRoundStatus.CRASHED;
                        _round.CrashedAt   = now;
                        _round.PhaseEndsAt = now + _live.CrashedMs;
                        foreach (var b in _bets.Where(b => b.Status == LiveBetStatus.Active))
                        {
                            b.Status = LiveBetStatus.Lost;
                            losers.Add(b);
                        }
                        crashed = (_round.Id, _round.CrashPoint,
                            _bets.Where(b => b.RoundId == _round.Id && b.Status is LiveBetStatus.CashedOut or LiveBetStatus.Lost).ToList());
                        _history.AddFirst(new { roundId = _round.Id, crashPoint = _round.CrashPoint });
                        while (_history.Count > _live.HistorySize) _history.RemoveLast();
                        round = PublicRound();
                        logger.LogInformation("Live crash: game={GameId} round={RoundId} crashed at {CrashPoint}x (nonce {Nonce})",
                            Game.GameId, _round.Id, _round.CrashPoint, _round.Proof.Nonce);
                    }
                    else tick = m;
                    break;

                case LiveRoundStatus.CRASHED when now >= _round.PhaseEndsAt:
                    _bets.RemoveAll(b => b.RoundId != 0 && b.Status is not LiveBetStatus.Pending);
                    StartBetting(now);
                    round = PublicRound();
                    break;
            }
        }

        if (tick is { } t)
            await hub.Clients.Group(Group).SendAsync("Tick", new { t = now, m = t });
        if (round != null)
            await hub.Clients.Group(Group).SendAsync("Round", round);
        foreach (var (bet, at) in autoCashOuts)
            _ = PayAsync(bet, at);
        foreach (var bet in losers)
        {
            if (bet.FreeBet != null) _ = promos.SettleFreeBetAsync(bet.FreeBet, bet.RoundId.ToString(), 0);
            await SendMyBet(bet, balance: null);
        }
        if (crashed is { } c)
            promos.RoundEnded(Game.GameId, c.RoundId.ToString(), c.CrashPoint,
                c.Bets.Select(b => new PromoBet(b.PlayerId, b.Username, b.Currency, b.Amount, b.FreeBet != null)).ToList());
    }

    private void StartBetting(long now)
    {
        var draw = rng.Draw(Game); // this game's own generator
        _round = new LiveRound
        {
            Id          = Math.Max(_round.Id + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - RoundEpoch),
            Status      = LiveRoundStatus.BETTING,
            PhaseEndsAt = now + _live.BettingMs,
            CrashPoint  = Math.Round(draw.Value, 2, MidpointRounding.ToZero),
            Proof       = draw.Proof,
        };
    }

    /// <summary>Displayed / payable multiplier, floored to the cent.</summary>
    private decimal MultiplierAt(long now)
    {
        var m = Math.Exp(_live.GrowthRate * ((now - _round.StartedAt) / 1000.0));
        return Math.Floor((decimal)Math.Min(m, 1_000_000d) * 100) / 100;
    }

    private object PublicRound() => new
    {
        gameId         = Game.GameId,
        id             = _round.Id,
        status         = _round.Status.ToString(),
        startedAt      = _round.StartedAt,
        phaseEndsAt    = _round.PhaseEndsAt,
        crashedAt      = _round.CrashedAt,
        crashPoint     = _round.Status == LiveRoundStatus.CRASHED ? _round.CrashPoint : (decimal?)null,
        serverSeedHash = _round.Proof.ServerSeedHash,
        nonce          = _round.Proof.Nonce,
    };

    // ── Snapshot ────────────────────────────────────────────────────────────

    public object Snapshot(string playerId)
    {
        var (hash, previous) = rng.GetSeedInfo(Game);
        lock (_lock)
        {
            return new
            {
                serverTime = Now(),
                round      = PublicRound(),
                multiplier = _round.Status == LiveRoundStatus.FLYING ? MultiplierAt(Now()) : (decimal?)null,
                history    = _history.ToList(),
                bets       = _bets.Where(b => b.Status != LiveBetStatus.Pending && b.RoundId == _round.Id).Select(PublicBet),
                myBets     = _bets.Where(b => b.PlayerId == playerId && b.Status != LiveBetStatus.Pending).Select(MyBetDto),
                fairness   = new { currentServerSeedHash = hash, previousServerSeed = previous },
            };
        }
    }

    // ── Player actions ──────────────────────────────────────────────────────

    public async Task<(object? Bet, decimal? Balance, string? Error)> PlaceBetAsync(PlayerSession player, int slot, decimal amount, decimal? autoCashOut, bool freeBet = false)
    {
        if (slot < 0 || slot >= _live.MaxBetsPerPlayer) return (null, null, $"Slot must be 0-{_live.MaxBetsPerPlayer - 1}.");
        if (autoCashOut is { } a && (a < _live.MinAutoCashOut || a != Math.Round(a, 2)))
            return (null, null, FormattableString.Invariant($"Auto cash-out must be at least {_live.MinAutoCashOut} with at most 2 decimals."));

        // A free bet's stake is the promo's: claim one first; its value is the bet amount, and no debit is made.
        PromoAward? free = null;
        if (freeBet)
        {
            lock (_lock)
                if (_round.Status != LiveRoundStatus.BETTING) return (null, null, "Betting is closed for this round.");
            free = await promos.ClaimFreeBetAsync(player, Game.GameId);
            if (free == null) return (null, null, "You have no free bet for this game.");
            amount = free.Amount;
        }

        LiveBet bet;
        lock (_lock)
        {
            string? error = null;
            if (_round.Status != LiveRoundStatus.BETTING) error = "Betting is closed for this round.";
            else if (_bets.Any(b => b.PlayerId == player.PlayerId && b.Slot == slot && b.RoundId == _round.Id && b.Status != LiveBetStatus.Cancelled))
                error = "You already have a bet in that slot.";
            if (error != null)
            {
                if (free != null) _ = promos.ReleaseFreeBetAsync(free);
                return (null, null, error);
            }
            // Reserved before the wallet call so a double-click can't place two bets in one slot.
            bet = new LiveBet
            {
                RoundId = _round.Id, PlayerId = player.PlayerId, Username = player.Username, Currency = player.Currency,
                Slot = slot, Amount = amount, AutoCashOut = autoCashOut, Status = LiveBetStatus.Pending, FreeBet = free,
            };
            _bets.Add(bet);
        }

        if (free != null)
        {
            bool placed;
            lock (_lock)
            {
                placed = _round.Id == bet.RoundId && _round.Status == LiveRoundStatus.BETTING;
                if (placed) bet.Status = LiveBetStatus.Active;
                else _bets.Remove(bet);
            }
            if (!placed)
            {
                await promos.ReleaseFreeBetAsync(free);
                return (null, null, "The round started before your free bet was placed — you still have it.");
            }
            promos.BetPlaced(Game.GameId, player.PlayerId, player.Username, player.Currency, amount, freeBet: true);
            await hub.Clients.Group(Group).SendAsync("BetPlaced", PublicBet(bet));
            return (MyBetDto(bet), null, null);
        }

        using var scope = scopes.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
        var debit = await wallet.DebitAsync(Game, player, bet.RoundId.ToString(), $"{bet.Id}-bet", amount);
        if (!debit.Ok)
        {
            lock (_lock) _bets.Remove(bet);
            return (null, debit.Balance, debit.Error);
        }

        bool accepted;
        lock (_lock)
        {
            accepted = _round.Id == bet.RoundId && _round.Status == LiveRoundStatus.BETTING;
            if (accepted) bet.Status = LiveBetStatus.Active;
            else _bets.Remove(bet);
        }
        if (accepted) promos.BetPlaced(Game.GameId, player.PlayerId, player.Username, player.Currency, amount, freeBet: false);

        if (!accepted)
        {
            var rb = await wallet.RollbackAsync(Game, player, $"{bet.Id}-rb", $"{bet.Id}-bet");
            if (!rb.Ok)
                logger.LogCritical("Live crash rollback FAILED: game={GameId} player={PlayerId} bet={BetId} code={Code}",
                    Game.GameId, player.PlayerId, bet.Id, rb.ErrorCode);
            return (null, rb.Balance, "The round started before your bet was placed — your stake was returned.");
        }

        await hub.Clients.Group(Group).SendAsync("BetPlaced", PublicBet(bet));
        return (MyBetDto(bet), debit.Balance, null);
    }

    public async Task<(decimal? Balance, string? Error)> CancelBetAsync(PlayerSession player, int slot)
    {
        LiveBet? bet;
        lock (_lock)
        {
            if (_round.Status != LiveRoundStatus.BETTING) return (null, "Bets can only be cancelled before the round starts.");
            bet = _bets.FirstOrDefault(b => b.PlayerId == player.PlayerId && b.Slot == slot && b.RoundId == _round.Id && b.Status == LiveBetStatus.Active);
            if (bet == null) return (null, "No bet to cancel in that slot.");
            bet.Status = LiveBetStatus.Cancelled; // out of the round from now on
        }

        if (bet.FreeBet != null)
        {
            // Nothing was debited — the free bet just goes back to the player.
            await promos.ReleaseFreeBetAsync(bet.FreeBet);
            await SendMyBet(bet, balance: null);
            return (null, null);
        }

        using var scope = scopes.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
        var rb = await wallet.RollbackAsync(Game, player, $"{bet.Id}-rb", $"{bet.Id}-bet");
        if (!rb.Ok)
        {
            // Couldn't refund — put the bet back so the stake isn't lost; it plays the round if betting has closed.
            lock (_lock) bet.Status = LiveBetStatus.Active;
            logger.LogError("Live crash cancel failed: game={GameId} bet={BetId} code={Code}", Game.GameId, bet.Id, rb.ErrorCode);
            return (null, rb.Error ?? "Could not cancel the bet.");
        }

        await SendMyBet(bet, rb.Balance);
        return (rb.Balance, null);
    }

    public async Task<(decimal? Multiplier, decimal? Win, decimal? Balance, string? Error)> CashOutAsync(PlayerSession player, int slot)
    {
        LiveBet? bet;
        decimal at;
        lock (_lock)
        {
            if (_round.Status != LiveRoundStatus.FLYING) return (null, null, null, "The round is not in flight.");
            bet = _bets.FirstOrDefault(b => b.PlayerId == player.PlayerId && b.Slot == slot && b.RoundId == _round.Id && b.Status == LiveBetStatus.Active);
            if (bet == null) return (null, null, null, "No active bet in that slot.");
            // The server's clock decides — never a multiplier sent by the client.
            at = MultiplierAt(Now());
            if (at >= _round.CrashPoint) return (null, null, null, "Too late — the round crashed.");
            bet.Status      = LiveBetStatus.CashedOut;
            bet.CashedOutAt = at;
        }

        var (win, balance) = await PayAsync(bet, at);
        return (at, win, balance, null);
    }

    // ── Payout ──────────────────────────────────────────────────────────────

    private async Task<(decimal Win, decimal? Balance)> PayAsync(LiveBet bet, decimal at)
    {
        var win = Math.Min(Math.Round(bet.Amount * at, 2, MidpointRounding.ToZero), Game.MaxWin);
        bet.Win = win;
        decimal? balance = null;

        if (bet.FreeBet != null)
        {
            // Promo money: the promotion pays what it allows (normally the winnings above the stake) and retries its own payout.
            bet.Win = await promos.SettleFreeBetAsync(bet.FreeBet, bet.RoundId.ToString(), win);
            await hub.Clients.Group(Group).SendAsync("CashedOut", PublicBet(bet));
            await SendMyBet(bet, balance);
            return (bet.Win.Value, balance);
        }

        try
        {
            using var scope = scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
            var player = new PlayerSession(bet.PlayerId, bet.Username, bet.Currency, Game.GameId);
            var credit = await wallet.CreditAsync(Game, player, bet.RoundId.ToString(), $"{bet.Id}-win", win);
            if (credit.Ok)
            {
                balance = credit.Balance;
                promos.BetSettled(Game.GameId, bet.PlayerId, bet.Username, bet.Currency, bet.RoundId.ToString(), bet.Amount, win, freeBet: false);
            }
            else
            {
                bet.WinPending = true;
                logger.LogCritical("Live crash win NOT credited: game={GameId} round={RoundId} player={PlayerId} bet={BetId} win={Win} code={Code} error={Error}",
                    Game.GameId, bet.RoundId, bet.PlayerId, bet.Id, win, credit.ErrorCode, credit.Error);
            }
        }
        catch (Exception ex)
        {
            bet.WinPending = true;
            logger.LogCritical(ex, "Live crash payout failed: game={GameId} bet={BetId}", Game.GameId, bet.Id);
        }

        await hub.Clients.Group(Group).SendAsync("CashedOut", PublicBet(bet));
        await SendMyBet(bet, balance);
        return (win, balance);
    }

    private Task SendMyBet(LiveBet bet, decimal? balance) =>
        hub.Clients.Group(PlayerGroup(Game.GameId, bet.PlayerId)).SendAsync("MyBet", new { bet = MyBetDto(bet), balance });

    private static object PublicBet(LiveBet b) => new
    {
        roundId     = b.RoundId,
        player      = Mask(b.Username),
        slot        = b.Slot,
        amount      = b.Amount,
        cashedOutAt = b.CashedOutAt,
        win         = b.Status == LiveBetStatus.CashedOut ? b.Win : null,
    };

    private object MyBetDto(LiveBet b) => new
    {
        gameId      = Game.GameId,
        id          = b.Id,
        roundId     = b.RoundId,
        slot        = b.Slot,
        amount      = b.Amount,
        autoCashOut = b.AutoCashOut,
        status      = b.Status.ToString(),
        cashedOutAt = b.CashedOutAt,
        win         = b.Win,
        winPending  = b.WinPending,
        freeBet     = b.FreeBet != null,
    };

    private static string Mask(string name) =>
        name.Length <= 2 ? name + "***" : name[..2] + "***";

    private sealed class LiveRound
    {
        public long Id { get; init; }
        public LiveRoundStatus Status { get; set; }
        public long StartedAt { get; set; }
        public long PhaseEndsAt { get; set; }
        public long? CrashedAt { get; set; }
        /// <summary>Secret until the crash.</summary>
        public decimal CrashPoint { get; init; }
        public RngProof Proof { get; init; } = new(string.Empty, null, 0);
    }

    private sealed class LiveBet
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public long RoundId { get; init; }
        public string PlayerId { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string Currency { get; init; } = string.Empty;
        public int Slot { get; init; }
        public decimal Amount { get; init; }
        public decimal? AutoCashOut { get; init; }
        public LiveBetStatus Status { get; set; }
        public decimal? CashedOutAt { get; set; }
        public decimal? Win { get; set; }
        public bool WinPending { get; set; }
        /// <summary>Set for a promo free bet: nothing was debited, and the promotion settles it.</summary>
        public PromoAward? FreeBet { get; init; }
    }
}
