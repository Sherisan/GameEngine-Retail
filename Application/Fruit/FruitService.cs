using System.Collections.Concurrent;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Blackjack;
using Worldiator.Server.Domain.Fruit;

namespace Worldiator.Server.Application.Fruit;

/// <summary>
/// JUICE RUSH (Engine = Fruit): one round per player and game at a time. See FruitRules for the math.
///
/// Bet: the stake is debited ("{round}-bet") and the round is bound to the player's committed server seed (its hash was
/// shown before the bet) and their client seed. Slice: the i-th fruit sliced rolls against that seed (FruitFair); a safe
/// fruit adds its boost, a bomb ends the round with nothing paid. Cash out, or reaching ×1000, credits the payout once
/// ("{round}-win"). The server seed is revealed when the round ends.
///
/// Which fruit is tossed is cosmetic and up to the frontend, so any fruit on the menu can be sliced: every kind returns
/// the same 97%.
///
/// Rounds live in memory (single node), like blackjack, DEFUSE, Vault Cracker and Deep Dive. A round left unfinished for
/// 10 minutes is cashed out at the multiplier it reached; before the first slice that returns the stake (×1).
/// </summary>
public sealed class FruitService(IServiceScopeFactory scopes, ILogger<FruitService> logger) : BackgroundService
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

    private sealed record Slice(FruitSpec Fruit, int Roll, bool Bomb);

    private sealed class Round
    {
        public required string RoundId { get; init; }
        public required GameDefinition Game { get; init; }
        public required string ServerSeed { get; init; }
        public required string ClientSeed { get; init; }
        public required long Nonce { get; init; }
        public required decimal Bet { get; init; }
        public List<Slice> Slices { get; } = [];
        public string Status { get; set; } = "live";
        public decimal Payout { get; set; }
        public decimal? Balance { get; set; }
        public bool WinPending { get; set; }
        public DateTime LastAt { get; set; } = DateTime.UtcNow;

        public bool Live => Status == "live";
        public bool AnySafe => Slices.Any(s => !s.Bomb);

        /// <summary>
        /// The cash-out multiplier of the safe slices (for a round that hit a bomb, what the player had), or ×1 for a
        /// round cashed out before its first slice: only the idle timeout does that, and it returns the stake.
        /// </summary>
        public decimal Multiplier => Status == "cashed" && !AnySafe
            ? 1m
            : FruitRules.Multiplier(Slices.Where(s => !s.Bomb).Select(s => s.Fruit));
    }

    private static string Key(string gameId, string playerId) => $"{gameId.ToLowerInvariant()}|{playerId}";

    private Seat SeatFor(GameDefinition game, PlayerSession player) =>
        _seats.GetOrAdd(Key(game.GameId, player.PlayerId), _ => new Seat { Player = player });

    // ── Public API (FruitController) ──────────────────────────────────────

    public Task<(object? State, WalletResult? Error)> StateAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, _ => Task.FromResult<WalletResult?>(null));

    public Task<(object? State, WalletResult? Error)> BetAsync(GameDefinition game, PlayerSession player, decimal stake) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is { Live: true }) return Bad("ROUND_IN_PLAY", "Finish the round in play first.");
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
                };
            }
            seat.NextServerSeed = Shoe.NewSeed();
            logger.LogInformation("Fruit bet: game={GameId} player={PlayerId} round={RoundId} stake={Stake} seedHash={Hash} nonce={Nonce}",
                game.GameId, player.PlayerId, roundId, stake, Shoe.Hash(seat.Round.ServerSeed), seat.Round.Nonce);
            return null;
        });

    public Task<(object? State, WalletResult? Error)> SliceAsync(GameDefinition game, PlayerSession player, string? kind) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no round in play — bet first.");
            if (FruitRules.Find(kind) is not { } fruit) return Bad("INVALID_FRUIT", "Unknown fruit.");
            round.LastAt = DateTime.UtcNow;
            var roll = FruitFair.Roll(round.ServerSeed, round.ClientSeed, round.Nonce, round.Slices.Count, fruit);
            var bomb = FruitFair.IsBomb(fruit, roll);
            round.Slices.Add(new Slice(fruit, roll, bomb));
            if (bomb) await SettleAsync(seat, round, "exploded");
            else if (FruitRules.IsMaxed(round.Multiplier)) await SettleAsync(seat, round, "maxed");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> CashOutAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no round in play — bet first.");
            if (!round.AnySafe) return Bad("NOT_ALLOWED", "Slice a fruit first.");
            await SettleAsync(seat, round, "cashed");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> SetClientSeedAsync(GameDefinition game, PlayerSession player, string? clientSeed) =>
        WithSeat(game, player, seat =>
        {
            var clean = clientSeed?.Trim() ?? "";
            if (seat.Round is { Live: true }) return Task.FromResult<WalletResult?>(Bad("ROUND_IN_PLAY", "Change seeds between rounds."));
            if (clean.Length is 0 or > 64) return Task.FromResult<WalletResult?>(Bad("INVALID_SEED", "The client seed must be 1-64 characters."));
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
        var multiplier = status == "exploded" ? 0m : round.Multiplier;
        round.Payout = Math.Min(FruitRules.Payout(round.Bet, multiplier), round.Game.MaxWin);
        var player = seat.Player;

        if (round.Payout > 0)
        {
            using var scope = scopes.CreateScope();
            var credit = await scope.ServiceProvider.GetRequiredService<WalletService>().CreditAsync(round.Game, player, round.RoundId, $"{round.RoundId}-win", round.Payout);
            if (credit.Ok) round.Balance = credit.Balance;
            else
            {
                round.WinPending = true;
                logger.LogCritical("Fruit win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    round.Game.GameId, player.PlayerId, round.RoundId, round.Payout, credit.ErrorCode, credit.Error);
            }
        }

        seat.History.Insert(0, new
        {
            id = round.RoundId,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            bet = round.Bet,
            slices = round.Slices.Select(s => new { fruit = s.Fruit.Kind, bomb = s.Bomb, roll = s.Roll }).ToList(),
            multiplier,
            payout = round.Payout,
            status,
            serverSeed = round.ServerSeed,
            serverSeedHash = Shoe.Hash(round.ServerSeed),
            clientSeed = round.ClientSeed,
            nonce = round.Nonce,
        });
        if (seat.History.Count > HistoryLimit) seat.History.RemoveAt(seat.History.Count - 1);

        logger.LogInformation("Fruit settle: game={GameId} player={PlayerId} round={RoundId} status={Status} stake={Stake} multiplier={Multiplier} win={Win} slices={Slices}",
            round.Game.GameId, player.PlayerId, round.RoundId, status, round.Bet, multiplier, round.Payout,
            string.Join(',', round.Slices.Select(s => s.Bomb ? $"{s.Fruit.Kind}!" : s.Fruit.Kind)));
    }

    // ── What the board shows ──────────────────────────────────────────────

    /// <summary>
    /// The board, copied while the seat is locked (it's serialized after the lock is released). The rolls stay hidden
    /// until the round ends and its seed is revealed in history.
    /// </summary>
    private static object Dto(Seat seat)
    {
        var r = seat.Round;
        return new
        {
            round = r == null ? null : new
            {
                id = r.RoundId,
                r.Bet,
                slices = r.Slices.Select(s => new { fruit = s.Fruit.Kind, bomb = s.Bomb }).ToList(),
                r.Multiplier,
                r.Payout,
                r.Status,
                r.Balance,
                r.WinPending,
            },
            fairness = new { serverSeedHash = Shoe.Hash(seat.NextServerSeed), clientSeed = seat.ClientSeed, nonce = seat.Nonce + 1 },
            history = seat.History.ToList(),
        };
    }

    private static WalletResult Bad(string code, string message) => WalletResult.Fail(WalletErrorKind.BadRequest, code, message);

    // ── Abandoned rounds ──────────────────────────────────────────────────

    /// <summary>Every minute: rounds idle for 10 minutes are cashed out, so a stake is never left hanging.</summary>
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
                        // Before the first slice this returns the stake (Round.Multiplier is ×1 then).
                        await SettleAsync(seat, round, "cashed");
                        logger.LogInformation("Fruit round {RoundId} for {PlayerId} was idle — cashed out at ×{Multiplier}", round.RoundId, seat.Player.PlayerId, round.Multiplier);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Fruit idle settle failed for {PlayerId}", seat.Player.PlayerId); }
                    finally { seat.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
