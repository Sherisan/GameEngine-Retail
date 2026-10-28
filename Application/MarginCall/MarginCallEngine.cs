using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.MarginCall;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Application.MarginCall;

/// <summary>
/// Authoritative Margin Call engine: owns the markets, the round lifecycle, every open position and
/// settlement. The TradeX frontend only renders what this broadcasts on MarginCallHub:
///   "Tick"    (all clients)  { t, p: prices in Markets order, m: public round multiplier }
///   "Round"   (all clients)  public round state on every phase change (crash data only once crashed)
///   "Settled" (one player)   that player's settled positions and new balance
/// Money moves through WalletService (ledger + SdGaming): one debit per position on open, one credit per
/// position with a non-zero return at settlement. Wallet round key: the round id.
///
/// Single-node: state lives in this process, so run one API instance for this game.
/// </summary>
public sealed class MarginCallEngine(
    IOptions<MarginCallOptions> options,
    IGameRegistry games,
    IGameRngService rng,
    IServiceScopeFactory scopes,
    IHubContext<MarginCallHub> hub,
    ILogger<MarginCallEngine> logger)
{
    // Round ids are seconds since this epoch (rounds are > 1s apart) so they never repeat across restarts —
    // the wallet ledger keys debits and credits by round.
    private static readonly long RoundEpoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private readonly MarginCallOptions _opt = options.Value;
    private readonly object _lock = new();
    private List<MarginMarket> _markets = [];
    private Dictionary<string, MarginMarket> _bySymbol = [];
    private MarginRound _round = new() { Status = MarginRoundStatus.RESULTS };
    private readonly List<MarginPosition> _positions = [];
    private long _crashAt, _closeAt;
    private bool _frozen;

    public string GameId => _opt.GameId;
    public MarginCallOptions Options => _opt;
    public GameDefinition? Game => games.Find(_opt.GameId);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task RunAsync(CancellationToken ct)
    {
        if (_opt.Markets.Count == 0)
        {
            logger.LogWarning("MarginCall: no Markets configured — engine not started.");
            return;
        }

        var now = Now();
        lock (_lock)
        {
            _markets  = _opt.Markets.Select(m => new MarginMarket(m, now, _opt.TickMs)).ToList();
            _bySymbol = _markets.ToDictionary(m => m.Config.Symbol, StringComparer.OrdinalIgnoreCase);
            StartWaiting(now);
        }
        logger.LogInformation("MarginCall engine started: {Markets} markets, game={GameId}", _markets.Count, _opt.GameId);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await StepAsync(); }
            catch (Exception ex) { logger.LogError(ex, "MarginCall tick failed"); }
        }
    }

    private async Task StepAsync()
    {
        var now = Now();
        double[] prices;
        double multiplier;
        object? roundChanged;
        (MarginRound Round, List<MarginPosition> Positions)? toSettle;

        lock (_lock)
        {
            // No position is open in RESULTS / WAITING, so only then may prices drift back toward base.
            var recenter = _round.Status is MarginRoundStatus.RESULTS or MarginRoundStatus.WAITING;
            if (!_frozen)
                foreach (var m in _markets) m.Tick(now, recenter);
            prices = _markets.Select(m => m.Price).ToArray();
            (roundChanged, toSettle) = AdvancePhase(now);
            multiplier = PublicMultiplier(now);
        }

        await hub.Clients.All.SendAsync("Tick", new { t = now, p = prices, m = multiplier });
        if (roundChanged != null)
            await hub.Clients.All.SendAsync("Round", roundChanged);
        if (toSettle is { } s)
            _ = SettleAsync(s.Round, s.Positions); // credits run alongside the loop; SETTLING covers the wait
    }

    // ── Round lifecycle (call under _lock) ──────────────────────────────────

    private (object? RoundChanged, (MarginRound, List<MarginPosition>)? ToSettle) AdvancePhase(long now)
    {
        switch (_round.Status)
        {
            case MarginRoundStatus.WAITING when now >= _round.PhaseEndsAt:
                var delay = CrashDelayMs(_round.CrashMultiplier);
                _round.StartedAt   = now;
                _crashAt           = now + delay;
                _closeAt           = _crashAt - _opt.ClosedMs;
                _round.Status      = MarginRoundStatus.TRADING;
                _round.PhaseEndsAt = _closeAt;
                return (PublicRound(), null);

            case MarginRoundStatus.TRADING or MarginRoundStatus.CLOSED when now >= _crashAt:
                _frozen              = true;
                _round.Status        = MarginRoundStatus.CRASHED;
                _round.CrashedAt     = now;
                _round.PhaseEndsAt   = now + _opt.CrashedMs;
                _round.CrashPrices   = _markets.ToDictionary(m => m.Config.Symbol, m => m.Price);
                return (PublicRound(), null);

            case MarginRoundStatus.TRADING when now >= _closeAt:
                _round.Status      = MarginRoundStatus.CLOSED;
                _round.PhaseEndsAt = _crashAt;
                return (PublicRound(), null);

            case MarginRoundStatus.CRASHED when now >= _round.PhaseEndsAt:
                _round.Status      = MarginRoundStatus.SETTLING;
                _round.PhaseEndsAt = now + _opt.SettlingMs;
                var settle = _positions.ToList();
                _positions.Clear();
                return (PublicRound(), (_round, settle));

            case MarginRoundStatus.SETTLING when now >= _round.PhaseEndsAt:
                _frozen            = false; // markets resume beneath the results screen
                _round.Status      = MarginRoundStatus.RESULTS;
                _round.PhaseEndsAt = now + _opt.ResultsMs;
                return (PublicRound(), null);

            case MarginRoundStatus.RESULTS when now >= _round.PhaseEndsAt:
                StartWaiting(now);
                return (PublicRound(), null);
        }
        return (null, null);
    }

    private void StartWaiting(long now)
    {
        var id = Math.Max(_round.Id + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - RoundEpoch);
        _round = new MarginRound
        {
            Id              = id,
            Status          = MarginRoundStatus.WAITING,
            CrashMultiplier = DrawCrashMultiplier(),
            PhaseEndsAt     = now + _opt.WaitingMs,
        };
        _crashAt = _closeAt = 0;
    }

    private double PublicMultiplier(long now) => _round.Status switch
    {
        MarginRoundStatus.TRADING => MultiplierAt(now - _round.StartedAt),
        MarginRoundStatus.CLOSED  => Math.Min(_round.CrashMultiplier, MultiplierAt(now - _round.StartedAt)),
        MarginRoundStatus.WAITING => 1,
        _                         => Math.Min(_round.CrashMultiplier, MultiplierAt((_round.CrashedAt ?? now) - _round.StartedAt)),
    };

    private double MultiplierAt(long elapsedMs) => Math.Exp(_opt.GrowthRate * (elapsedMs / 1000.0));

    private long CrashDelayMs(double crashMultiplier)
    {
        var raw = Math.Log(crashMultiplier) / _opt.GrowthRate * 1000;
        return (long)Math.Min(_opt.MaxTradingMs, Math.Max(_opt.MinOpenMs + _opt.ClosedMs, raw));
    }

    /// <summary>
    /// Round length. Drawn from this game's own RNG instance (its "Rng" Target block) and clamped to the
    /// playable 1.25×–14× range; without an Rng block, the original client's long-tailed formula.
    /// It only decides when the round ends — payouts come from the price move.
    /// </summary>
    private double DrawCrashMultiplier()
    {
        var game = Game;
        if (game?.Rng != null)
            return Math.Min(14, Math.Max(1.25, (double)rng.Draw(game).Value));
        return GenerateCrashMultiplier();
    }

    private static double GenerateCrashMultiplier()
    {
        var r = Math.Min(0.999999, Math.Max(1e-9, MarginMarket.NextDouble()));
        var raw = 1.2 + -Math.Log(1 - r) * 1.6;
        return Math.Min(14, Math.Max(1.25, Math.Round(raw, 2)));
    }

    /// <summary>What every client may see. Crash multiplier and crash prices only once the round has crashed.</summary>
    private object PublicRound()
    {
        var crashed = _round.Status is MarginRoundStatus.CRASHED or MarginRoundStatus.SETTLING or MarginRoundStatus.RESULTS;
        return new
        {
            id              = _round.Id,
            status          = _round.Status.ToString(),
            startedAt       = _round.StartedAt,
            phaseEndsAt     = _round.PhaseEndsAt,
            crashedAt       = _round.CrashedAt,
            crashMultiplier = crashed ? Math.Min(_round.CrashMultiplier, PublicMultiplier(_round.CrashedAt ?? Now())) : (double?)null,
            crashPrices     = crashed ? _round.CrashPrices : null,
        };
    }

    // ── Player actions ──────────────────────────────────────────────────────

    public object Snapshot(string playerId)
    {
        lock (_lock)
        {
            return new
            {
                serverTime = Now(),
                round      = PublicRound(),
                multiplier = PublicMultiplier(Now()),
                markets    = _markets.Select(m => new { symbol = m.Config.Symbol, price = m.Price, candles = m.Candles() }),
                positions  = _positions.Where(p => p.PlayerId == playerId).Select(PositionDto),
            };
        }
    }

    public async Task<(object? Position, decimal? Balance, string? Error)> OpenPositionAsync(
        PlayerSession player, string market, string direction, decimal stake, int multiplier)
    {
        var game = Game;
        if (game == null) return (null, null, "Game is not enabled.");
        direction = (direction ?? string.Empty).ToUpperInvariant();
        if (direction is not ("BUY" or "SELL")) return (null, null, "Direction must be BUY or SELL.");
        if (!_opt.AllowedMultipliers.Contains(multiplier)) return (null, null, $"Multiplier must be one of {string.Join(", ", _opt.AllowedMultipliers)}.");

        long roundId;
        lock (_lock)
        {
            if (!_bySymbol.ContainsKey(market ?? string.Empty)) return (null, null, "Unknown market.");
            if (_round.Status != MarginRoundStatus.TRADING) return (null, null, "Trading is currently closed.");
            if (_positions.Count(p => p.PlayerId == player.PlayerId) >= _opt.MaxPositionsPerRound)
                return (null, null, $"At most {_opt.MaxPositionsPerRound} positions per round.");
            roundId = _round.Id;
        }

        var positionId = Guid.NewGuid().ToString("N");
        using var scope = scopes.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();

        var debit = await wallet.DebitAsync(game, player, roundId.ToString(), $"{positionId}-bet", stake);
        if (!debit.Ok) return (null, null, debit.Error);

        // The entry price is taken after the stake is secured, at the server's current price.
        MarginPosition? position = null;
        lock (_lock)
        {
            if (_round.Id == roundId && _round.Status == MarginRoundStatus.TRADING)
            {
                var mid = _bySymbol[market!].Price;
                position = new MarginPosition
                {
                    Id         = positionId,
                    RoundId    = roundId,
                    PlayerId   = player.PlayerId,
                    Username   = player.Username,
                    Currency   = player.Currency,
                    Market     = _bySymbol[market!].Config.Symbol,
                    Direction  = direction,
                    Stake      = stake,
                    Multiplier = multiplier,
                    EntryPrice = direction == "BUY" ? Ask(mid) : Bid(mid),
                    OpenedAt   = Now(),
                };
                _positions.Add(position);
            }
        }

        if (position == null)
        {
            // Trading closed while the wallet call was in flight — give the stake back.
            var rb = await wallet.RollbackAsync(game, player, $"{positionId}-rb", $"{positionId}-bet");
            if (!rb.Ok)
                logger.LogCritical("MarginCall rollback FAILED: player={PlayerId} round={RoundId} position={PositionId} code={Code}",
                    player.PlayerId, roundId, positionId, rb.ErrorCode);
            return (null, rb.Balance, "Trading closed before your trade was placed — your stake was returned.");
        }

        logger.LogInformation("MarginCall open: round={RoundId} player={PlayerId} {Direction} {Market} stake={Stake} x{Multiplier} entry={Entry}",
            roundId, player.PlayerId, direction, position.Market, stake, multiplier, position.EntryPrice);
        return (PositionDto(position), debit.Balance, null);
    }

    // ── Settlement ──────────────────────────────────────────────────────────

    private async Task SettleAsync(MarginRound round, List<MarginPosition> positions)
    {
        var game = Game;
        if (game == null || positions.Count == 0) return;
        var closedAt = round.CrashedAt ?? Now();

        foreach (var byPlayer in positions.GroupBy(p => p.PlayerId))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
                var first  = byPlayer.First();
                var player = new PlayerSession(first.PlayerId, first.Username, first.Currency, game.GameId);

                var settled = new List<MarginSettledPosition>();
                decimal? balance = null;
                decimal credited = 0;
                foreach (var p in byPlayer)
                {
                    var (exit, commission, ret) = Settle(p, round.CrashPrices![p.Market]);
                    ret = Math.Min(ret, game.MaxWin - credited); // per-round cap, as the ledger enforces
                    var pending = false;
                    if (ret > 0)
                    {
                        var credit = await wallet.CreditAsync(game, player, round.Id.ToString(), $"{p.Id}-win", ret);
                        if (credit.Ok) { credited += ret; balance = credit.Balance ?? balance; }
                        else
                        {
                            pending = true;
                            logger.LogCritical("MarginCall credit NOT paid: round={RoundId} player={PlayerId} position={PositionId} amount={Amount} code={Code} error={Error}",
                                round.Id, p.PlayerId, p.Id, ret, credit.ErrorCode, credit.Error);
                        }
                    }

                    var pnl = ret - p.Stake;
                    settled.Add(new MarginSettledPosition(p.Id, p.RoundId, p.Market, p.Direction, p.Stake, p.Multiplier,
                        p.EntryPrice, exit, commission, pnl, ret, pnl > 0 ? "WIN" : pnl < 0 ? "LOSS" : "PUSH",
                        p.OpenedAt, closedAt, pending));
                    logger.LogInformation("MarginCall settle: round={RoundId} player={PlayerId} position={PositionId} entry={Entry} exit={Exit} return={Return}",
                        round.Id, p.PlayerId, p.Id, p.EntryPrice, exit, ret);
                }

                balance ??= await wallet.GetBalanceAsync(player);
                await hub.Clients.User(player.PlayerId).SendAsync("Settled", new { roundId = round.Id, positions = settled, balance });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MarginCall settlement failed: round={RoundId} player={PlayerId}", round.Id, byPlayer.Key);
            }
        }
    }

    /// <summary>
    /// P/L = stake × multiplier × move, move measured from entry (ask/bid) to exit (bid/ask), loss capped at the stake.
    /// Return = stake + P/L − commission, never below zero. Rounded down to the cent.
    /// </summary>
    private (double Exit, decimal Commission, decimal Return) Settle(MarginPosition p, double crashMid)
    {
        var buy  = p.Direction == "BUY";
        var exit = buy ? Bid(crashMid) : Ask(crashMid);
        var move = buy ? (exit - p.EntryPrice) / p.EntryPrice : (p.EntryPrice - exit) / p.EntryPrice;
        var pnl  = Math.Max(-(double)p.Stake, (double)p.Stake * p.Multiplier * move);
        var commission = Math.Round(p.Stake * _opt.CommissionPercent / 100m, 2, MidpointRounding.ToZero);
        var ret = Math.Max(0m, Math.Round(p.Stake + (decimal)pnl - commission, 2, MidpointRounding.ToZero));
        return (exit, commission, ret);
    }

    private double HalfSpread => _opt.SpreadBps / 20000.0;
    private double Ask(double mid) => mid * (1 + HalfSpread);
    private double Bid(double mid) => mid * (1 - HalfSpread);

    private static object PositionDto(MarginPosition p) => new
    {
        id         = p.Id,
        roundId    = p.RoundId,
        market     = p.Market,
        direction  = p.Direction,
        stake      = p.Stake,
        multiplier = p.Multiplier,
        entryPrice = p.EntryPrice,
        openedAt   = p.OpenedAt,
    };
}
