using System.Collections.Concurrent;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Blackjack;
using Worldiator.Server.Domain.Dive;

namespace Worldiator.Server.Application.Dive;

/// <summary>
/// DEEP DIVE (Engine = Dive): one dive per player and game at a time. See DiveRules for the math.
///
/// Bet: the stake is debited ("{round}-bet") and the shark's cave in every zone is drawn from the player's committed
/// server seed (its hash was shown before the bet) and their client seed. Pick: a safe cave clears the zone, the shark's
/// cave ends the dive with nothing paid. Cash out (surfacing) or reaching the abyss credits the payout once
/// ("{round}-win"). The server seed is revealed when the dive ends.
///
/// Dives live in memory (single node), like blackjack, DEFUSE and Vault Cracker. A dive left unfinished for 10 minutes
/// is surfaced at the rung it reached; at rung 0 that returns the stake.
/// </summary>
public sealed class DiveService(IServiceScopeFactory scopes, ILogger<DiveService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private const int HistoryLimit = 50;
    private readonly ConcurrentDictionary<string, Seat> _seats = new();

    private sealed class Seat
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public required PlayerSession Player { get; init; }
        public string NextServerSeed { get; set; } = Shoe.NewSeed();
        public string ClientSeed { get; set; } = Shoe.NewSeed()[..16];
        public long Nonce { get; set; }
        public Round? Round { get; set; }
        public List<object> History { get; } = [];
    }

    private sealed class Round
    {
        public required string RoundId { get; init; }
        public required GameDefinition Game { get; init; }
        public required string ServerSeed { get; init; }
        public required string ClientSeed { get; init; }
        public required long Nonce { get; init; }
        public required decimal Bet { get; init; }
        public required int[] Sharks { get; init; }
        public List<int> Picks { get; } = [];
        public string Status { get; set; } = "live";
        public decimal Payout { get; set; }
        public decimal? Balance { get; set; }
        public bool WinPending { get; set; }
        public DateTime LastAt { get; set; } = DateTime.UtcNow;

        public bool Live => Status == "live";
        /// <summary>Zones cleared. The cave with the shark doesn't count.</summary>
        public int Rung => Status == "bitten" ? Picks.Count - 1 : Picks.Count;
    }

    private static string Key(string gameId, string playerId) => $"{gameId.ToLowerInvariant()}|{playerId}";

    private Seat SeatFor(GameDefinition game, PlayerSession player) =>
        _seats.GetOrAdd(Key(game.GameId, player.PlayerId), _ => new Seat { Player = player });

    // ── Public API (DiveController) ───────────────────────────────────────

    public Task<(object? State, WalletResult? Error)> StateAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, _ => Task.FromResult<WalletResult?>(null));

    public Task<(object? State, WalletResult? Error)> BetAsync(GameDefinition game, PlayerSession player, decimal stake) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is { Live: true }) return Bad("ROUND_IN_PLAY", "Finish the dive in progress first.");
            if (stake <= 0 || stake != Math.Round(stake, 2))
                return Bad("INVALID_AMOUNT", "The stake must be a positive amount with at most 2 decimals.");
            if (stake < game.MinBet || stake > game.MaxBet)
                return Bad("INVALID_AMOUNT", FormattableString.Invariant($"The stake must be between {game.MinBet:0.00} and {game.MaxBet:0.00}."));

            var roundId = Guid.NewGuid().ToString("N");
            using (var scope = scopes.CreateScope())
            {
                var debit = await scope.ServiceProvider.GetRequiredService<WalletService>().DebitAsync(game, player, roundId, $"{roundId}-bet", stake);
                if (!debit.Ok) return debit;
                seat.Nonce++;
                seat.Round = new Round
                {
                    RoundId = roundId, Game = game, Bet = stake, Balance = debit.Balance,
                    ServerSeed = seat.NextServerSeed, ClientSeed = seat.ClientSeed, Nonce = seat.Nonce,
                    Sharks = DiveFair.Sharks(seat.NextServerSeed, seat.ClientSeed, seat.Nonce),
                };
            }
            seat.NextServerSeed = Shoe.NewSeed();
            logger.LogInformation("Dive bet: game={GameId} player={PlayerId} round={RoundId} stake={Stake} seedHash={Hash} nonce={Nonce}",
                game.GameId, player.PlayerId, roundId, stake, Shoe.Hash(seat.Round.ServerSeed), seat.Round.Nonce);
            return null;
        });

    public Task<(object? State, WalletResult? Error)> PickAsync(GameDefinition game, PlayerSession player, int cave) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no dive in progress — bet first.");
            if (cave is < 0 or >= DiveRules.Caves) return Bad("INVALID_CAVE", "Unknown cave.");
            round.LastAt = DateTime.UtcNow;
            var zone = round.Picks.Count;
            round.Picks.Add(cave);
            if (cave == round.Sharks[zone]) await SettleAsync(seat, round, "bitten");
            else if (round.Picks.Count == DiveRules.Zones) await SettleAsync(seat, round, "abyss");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> CashOutAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no dive in progress — bet first.");
            if (round.Rung == 0) return Bad("NOT_ALLOWED", "Clear at least one zone first.");
            await SettleAsync(seat, round, "surfaced");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> SetClientSeedAsync(GameDefinition game, PlayerSession player, string? clientSeed) =>
        WithSeat(game, player, seat =>
        {
            var clean = clientSeed?.Trim() ?? "";
            if (seat.Round is { Live: true }) return Task.FromResult(Bad("ROUND_IN_PLAY", "Change seeds between dives."));
            if (clean.Length is 0 or > 64) return Task.FromResult(Bad("INVALID_SEED", "The client seed must be 1-64 characters."));
            seat.ClientSeed = clean;
            return Task.FromResult<WalletResult?>(null);
        });

    /// <summary>Runs one action under the seat's lock and returns the new state, or the action's error.</summary>
    private async Task<(object? State, WalletResult? Error)> WithSeat(GameDefinition game, PlayerSession player, Func<Seat, Task<WalletResult?>> action)
    {
        var seat = SeatFor(game, player);
        await seat.Gate.WaitAsync();
        try
        {
            var error = await action(seat);
            return error != null ? (null, error) : (Dto(seat), null);
        }
        finally { seat.Gate.Release(); }
    }

    // ── Settlement ────────────────────────────────────────────────────────

    private async Task SettleAsync(Seat seat, Round round, string status)
    {
        round.Status = status;
        round.Payout = status == "bitten" ? 0m : Math.Min(DiveRules.Payout(round.Bet, round.Rung), round.Game.MaxWin);
        var player = seat.Player;

        if (round.Payout > 0)
        {
            using var scope = scopes.CreateScope();
            var credit = await scope.ServiceProvider.GetRequiredService<WalletService>().CreditAsync(round.Game, player, round.RoundId, $"{round.RoundId}-win", round.Payout);
            if (credit.Ok) round.Balance = credit.Balance;
            else
            {
                round.WinPending = true;
                logger.LogCritical("Dive win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    round.Game.GameId, player.PlayerId, round.RoundId, round.Payout, credit.ErrorCode, credit.Error);
            }
        }

        seat.History.Insert(0, new
        {
            id = round.RoundId,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            bet = round.Bet,
            picks = round.Picks.ToList(),
            rung = round.Rung,
            payout = round.Payout,
            status,
            sharks = round.Sharks,
            serverSeed = round.ServerSeed,
            serverSeedHash = Shoe.Hash(round.ServerSeed),
            clientSeed = round.ClientSeed,
            nonce = round.Nonce,
        });
        if (seat.History.Count > HistoryLimit) seat.History.RemoveAt(seat.History.Count - 1);

        logger.LogInformation("Dive settle: game={GameId} player={PlayerId} round={RoundId} status={Status} stake={Stake} win={Win} picks={Picks} sharks={Sharks}",
            round.Game.GameId, player.PlayerId, round.RoundId, status, round.Bet, round.Payout,
            string.Join(',', round.Picks), string.Join(',', round.Sharks));
    }

    // ── What the ocean shows ──────────────────────────────────────────────

    private static object Dto(Seat seat)
    {
        var r = seat.Round;
        return new
        {
            round = r == null ? null : new
            {
                id = r.RoundId,
                r.Bet,
                picks = r.Picks,
                rung = r.Rung,
                r.Payout,
                r.Status,
                sharks = r.Live ? null : r.Sharks,
                r.Balance,
                r.WinPending,
            },
            fairness = new { serverSeedHash = Shoe.Hash(seat.NextServerSeed), clientSeed = seat.ClientSeed, nonce = seat.Nonce + 1 },
            history = seat.History,
        };
    }

    private static WalletResult Bad(string code, string message) => WalletResult.Fail(WalletErrorKind.BadRequest, code, message);

    // ── Abandoned dives ───────────────────────────────────────────────────

    /// <summary>Every minute: dives idle for 10 minutes are surfaced at their current rung, so a stake is never left hanging.</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var seat in _seats.Values)
                {
                    if (seat.Round is not { Live: true } r || DateTime.UtcNow - r.LastAt < IdleTimeout) continue;
                    await seat.Gate.WaitAsync(ct);
                    try
                    {
                        if (seat.Round is not { Live: true } round) continue;
                        // Multiplier(0) is 1, so a dive that never left the surface pays the stake back.
                        await SettleAsync(seat, round, "surfaced");
                        logger.LogInformation("Dive {RoundId} for {PlayerId} was idle — surfaced at rung {Rung}", round.RoundId, seat.Player.PlayerId, round.Rung);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Dive idle settle failed for {PlayerId}", seat.Player.PlayerId); }
                    finally { seat.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
