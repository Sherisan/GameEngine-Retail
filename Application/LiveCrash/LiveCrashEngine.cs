using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
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
    IGameRngService rng,
    IServiceScopeFactory scopes,
    IHubContext<LiveCrashHub> hub,
    ILogger logger)
{
    private static readonly long RoundEpoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private readonly LiveCrashSettings _live = game.Live ?? new LiveCrashSettings();
    private readonly object _lock = new();
    private readonly List<LiveBet> _bets = [];
    private readonly LinkedList<object> _history = new();
    private LiveRound _round = new() { Status = LiveRoundStatus.CRASHED };

    public GameDefinition Game => game;
    public LiveCrashSettings Settings => _live;
    public string Group => GroupFor(game.GameId);
    public static string GroupFor(string gameId) => $"live:{gameId}";
    public static string PlayerGroup(string gameId, string playerId) => $"live:{gameId}:p:{playerId}";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task RunAsync(CancellationToken ct)
    {
        lock (_lock) StartBetting(Now());
        await hub.Clients.Group(Group).SendAsync("Round", PublicRound(), ct);
        logger.LogInformation("Live crash engine started: game={GameId}", game.GameId);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_live.TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await StepAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Live crash tick failed: game={GameId}", game.GameId); }
        }
    }

    private async Task StepAsync()
    {
        var now = Now();
        object? round = null;
        decimal? tick = null;
        List<(LiveBet Bet, decimal At)> autoCashOuts = [];
        List<LiveBet> losers = [];

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
                        _history.AddFirst(new { roundId = _round.Id, crashPoint = _round.CrashPoint });
                        while (_history.Count > _live.HistorySize) _history.RemoveLast();
                        round = PublicRound();
                        logger.LogInformation("Live crash: game={GameId} round={RoundId} crashed at {CrashPoint}x (nonce {Nonce})",
                            game.GameId, _round.Id, _round.CrashPoint, _round.Proof.Nonce);
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
            await SendMyBet(bet, balance: null);
    }

    private void StartBetting(long now)
    {
        var draw = rng.Draw(game); // this game's own generator
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
        gameId         = game.GameId,
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
        var (hash, previous) = rng.GetSeedInfo(game);
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

    public async Task<(object? Bet, decimal? Balance, string? Error)> PlaceBetAsync(PlayerSession player, int slot, decimal amount, decimal? autoCashOut)
    {
        if (slot < 0 || slot >= _live.MaxBetsPerPlayer) return (null, null, $"Slot must be 0-{_live.MaxBetsPerPlayer - 1}.");
        if (autoCashOut is { } a && (a < _live.MinAutoCashOut || a != Math.Round(a, 2)))
            return (null, null, FormattableString.Invariant($"Auto cash-out must be at least {_live.MinAutoCashOut} with at most 2 decimals."));

        LiveBet bet;
        lock (_lock)
        {
            if (_round.Status != LiveRoundStatus.BETTING) return (null, null, "Betting is closed for this round.");
            if (_bets.Any(b => b.PlayerId == player.PlayerId && b.Slot == slot && b.RoundId == _round.Id && b.Status != LiveBetStatus.Cancelled))
                return (null, null, "You already have a bet in that slot.");
            // Reserved before the wallet call so a double-click can't place two bets in one slot.
            bet = new LiveBet
            {
                RoundId = _round.Id, PlayerId = player.PlayerId, Username = player.Username, Currency = player.Currency,
                Slot = slot, Amount = amount, AutoCashOut = autoCashOut, Status = LiveBetStatus.Pending,
            };
            _bets.Add(bet);
        }

        using var scope = scopes.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
        var debit = await wallet.DebitAsync(game, player, bet.RoundId.ToString(), $"{bet.Id}-bet", amount);
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

        if (!accepted)
        {
            var rb = await wallet.RollbackAsync(game, player, $"{bet.Id}-rb", $"{bet.Id}-bet");
            if (!rb.Ok)
                logger.LogCritical("Live crash rollback FAILED: game={GameId} player={PlayerId} bet={BetId} code={Code}",
                    game.GameId, player.PlayerId, bet.Id, rb.ErrorCode);
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

        using var scope = scopes.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
        var rb = await wallet.RollbackAsync(game, player, $"{bet.Id}-rb", $"{bet.Id}-bet");
        if (!rb.Ok)
        {
            // Couldn't refund — put the bet back so the stake isn't lost; it plays the round if betting has closed.
            lock (_lock) bet.Status = LiveBetStatus.Active;
            logger.LogError("Live crash cancel failed: game={GameId} bet={BetId} code={Code}", game.GameId, bet.Id, rb.ErrorCode);
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
        var win = Math.Min(Math.Round(bet.Amount * at, 2, MidpointRounding.ToZero), game.MaxWin);
        bet.Win = win;
        decimal? balance = null;
        try
        {
            using var scope = scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
            var player = new PlayerSession(bet.PlayerId, bet.Username, bet.Currency, game.GameId);
            var credit = await wallet.CreditAsync(game, player, bet.RoundId.ToString(), $"{bet.Id}-win", win);
            if (credit.Ok) balance = credit.Balance;
            else
            {
                bet.WinPending = true;
                logger.LogCritical("Live crash win NOT credited: game={GameId} round={RoundId} player={PlayerId} bet={BetId} win={Win} code={Code} error={Error}",
                    game.GameId, bet.RoundId, bet.PlayerId, bet.Id, win, credit.ErrorCode, credit.Error);
            }
        }
        catch (Exception ex)
        {
            bet.WinPending = true;
            logger.LogCritical(ex, "Live crash payout failed: game={GameId} bet={BetId}", game.GameId, bet.Id);
        }

        await hub.Clients.Group(Group).SendAsync("CashedOut", PublicBet(bet));
        await SendMyBet(bet, balance);
        return (win, balance);
    }

    private Task SendMyBet(LiveBet bet, decimal? balance) =>
        hub.Clients.Group(PlayerGroup(game.GameId, bet.PlayerId)).SendAsync("MyBet", new { bet = MyBetDto(bet), balance });

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
        gameId      = game.GameId,
        id          = b.Id,
        roundId     = b.RoundId,
        slot        = b.Slot,
        amount      = b.Amount,
        autoCashOut = b.AutoCashOut,
        status      = b.Status.ToString(),
        cashedOutAt = b.CashedOutAt,
        win         = b.Win,
        winPending  = b.WinPending,
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
    }
}
