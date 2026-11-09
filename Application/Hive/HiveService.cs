using System.Collections.Concurrent;
using System.Numerics;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Blackjack;
using Worldiator.Server.Domain.Hive;

namespace Worldiator.Server.Application.Hive;

/// <summary>
/// HIVE MIND (Engine = Hive): one round per player and game at a time. See HiveRules for the math.
///
/// Bet: the stake is debited ("{round}-bet") and the bees are hidden by the player's committed server seed (its hash was
/// shown before the bet) and their client seed. Pick: a honey cell opens with its clue (and floods out from clues of 0)
/// and multiplies the fair value by its boost, worked out from every clue on the board at that moment; a bee ends the
/// round with nothing paid. Cash out, clearing every honey cell, or reaching ×500 credits the payout once
/// ("{round}-win"). The server seed is revealed when the round ends.
///
/// Rounds live in memory (single node), like blackjack, DEFUSE, Vault Cracker, Deep Dive and Juice Rush. A round left
/// unfinished for 10 minutes is cashed out at the multiplier it reached; before the first pick that returns the stake (×1).
/// </summary>
public sealed class HiveService(IServiceScopeFactory scopes, ILogger<HiveService> logger) : BackgroundService
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

    private sealed record Pick(int Cell, bool Bee);

    private sealed class Round
    {
        public required string RoundId { get; init; }
        public required GameDefinition Game { get; init; }
        public required string ServerSeed { get; init; }
        public required string ClientSeed { get; init; }
        public required long Nonce { get; init; }
        public required decimal Bet { get; init; }
        public required int Level { get; init; }
        public required int[] BeeCells { get; init; }
        public int BeeMask => BeeCells.Aggregate(0, (mask, c) => mask | (1 << c));
        public HiveInfo Info { get; } = new();
        public List<Pick> Picks { get; } = [];
        public int HoneyPicks { get; set; }
        /// <summary>The fair value, Num / Den: the stake times every boost so far.</summary>
        public BigInteger Num { get; set; } = BigInteger.One;
        public BigInteger Den { get; set; } = BigInteger.One;
        public string Status { get; set; } = "live";
        public decimal Payout { get; set; }
        public decimal? Balance { get; set; }
        public bool WinPending { get; set; }
        public DateTime LastAt { get; set; } = DateTime.UtcNow;

        public bool Live => Status == "live";

        /// <summary>
        /// The cash-out multiplier of the fair value (for a stung round, what the player had); 0 before the first honey,
        /// except a round cashed out before its first pick: only the idle timeout does that, and it returns the stake (×1).
        /// </summary>
        public decimal Multiplier => HoneyPicks > 0 ? HiveRules.Multiplier(Num, Den) : Status == "cashed" ? 1m : 0m;
    }

    private static string Key(string gameId, string playerId) => $"{gameId.ToLowerInvariant()}|{playerId}";

    private Seat SeatFor(GameDefinition game, PlayerSession player) =>
        _seats.GetOrAdd(Key(game.GameId, player.PlayerId), _ => new Seat { Player = player });

    // ── Public API (HiveController) ───────────────────────────────────────

    public Task<(object? State, WalletResult? Error)> StateAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, _ => Task.FromResult<WalletResult?>(null));

    public Task<(object? State, WalletResult? Error)> BetAsync(GameDefinition game, PlayerSession player, decimal stake, int bees) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is { Live: true }) return Bad("ROUND_IN_PLAY", "Finish the round in play first.");
            if (!HiveRules.IsLevel(bees))
                return Bad("INVALID_BEES", $"Choose {string.Join(", ", HiveRules.Levels[..^1])} or {HiveRules.Levels[^1]} bees.");
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
                    RoundId = roundId, Game = game, Bet = stake, Balance = debit.Balance, Level = bees,
                    ServerSeed = seat.NextServerSeed, ClientSeed = seat.ClientSeed, Nonce = seat.Nonce,
                    BeeCells = HiveFair.BeeCells(seat.NextServerSeed, seat.ClientSeed, seat.Nonce, bees),
                };
            }
            seat.NextServerSeed = Shoe.NewSeed();
            logger.LogInformation("Hive bet: game={GameId} player={PlayerId} round={RoundId} stake={Stake} bees={Bees} seedHash={Hash} nonce={Nonce}",
                game.GameId, player.PlayerId, roundId, stake, bees, Shoe.Hash(seat.Round.ServerSeed), seat.Round.Nonce);
            return null;
        });

    public Task<(object? State, WalletResult? Error)> PickAsync(GameDefinition game, PlayerSession player, int cell) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no round in play — bet first.");
            if (cell is < 0 or >= HiveBoard.CellCount) return Bad("INVALID_CELL", "Unknown cell.");
            if (HiveBoard.Has(round.Info.Opened, cell)) return Bad("INVALID_CELL", "That cell is already open.");
            round.LastAt = DateTime.UtcNow;

            // The odds the player is betting at: everything the board shows before this pick.
            var (total, bees) = HiveRules.Odds(round.Level, round.Info);
            var bee = HiveBoard.Has(round.BeeMask, cell);
            round.Picks.Add(new Pick(cell, bee));
            if (bee)
            {
                await SettleAsync(seat, round, "stung");
                return null;
            }
            round.Num *= total;
            round.Den *= total - bees[cell];
            round.HoneyPicks++;
            round.Info.Open(cell, round.BeeMask);
            if (HiveRules.IsMaxed(round.Multiplier)) await SettleAsync(seat, round, "maxed");
            else if (HiveBoard.Count(round.Info.Opened) == HiveBoard.CellCount - round.Level) await SettleAsync(seat, round, "cleared");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> CashOutAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no round in play — bet first.");
            if (round.HoneyPicks == 0) return Bad("NOT_ALLOWED", "Open a cell first.");
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
        var multiplier = status == "stung" ? 0m : round.Multiplier;
        round.Payout = Math.Min(HiveRules.Payout(round.Bet, multiplier), round.Game.MaxWin);
        var player = seat.Player;

        if (round.Payout > 0)
        {
            using var scope = scopes.CreateScope();
            var credit = await scope.ServiceProvider.GetRequiredService<WalletService>().CreditAsync(round.Game, player, round.RoundId, $"{round.RoundId}-win", round.Payout);
            if (credit.Ok) round.Balance = credit.Balance;
            else
            {
                round.WinPending = true;
                logger.LogCritical("Hive win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    round.Game.GameId, player.PlayerId, round.RoundId, round.Payout, credit.ErrorCode, credit.Error);
            }
        }

        seat.History.Insert(0, new
        {
            id = round.RoundId,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            bet = round.Bet,
            bees = round.Level,
            picks = round.Picks.Select(p => new { cell = p.Cell, bee = p.Bee }).ToList(),
            multiplier,
            payout = round.Payout,
            status,
            beeCells = round.BeeCells.Order().ToList(),
            serverSeed = round.ServerSeed,
            serverSeedHash = Shoe.Hash(round.ServerSeed),
            clientSeed = round.ClientSeed,
            nonce = round.Nonce,
        });
        if (seat.History.Count > HistoryLimit) seat.History.RemoveAt(seat.History.Count - 1);

        logger.LogInformation("Hive settle: game={GameId} player={PlayerId} round={RoundId} status={Status} stake={Stake} bees={Bees} multiplier={Multiplier} win={Win} picks={Picks}",
            round.Game.GameId, player.PlayerId, round.RoundId, status, round.Bet, round.Level, multiplier, round.Payout,
            string.Join(',', round.Picks.Select(p => p.Bee ? $"{p.Cell}!" : $"{p.Cell}")));
    }

    // ── What the hive shows ───────────────────────────────────────────────

    /// <summary>
    /// The board, copied while the seat is locked (it's serialized after the lock is released). The bees stay hidden
    /// until the round ends.
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
                bees = r.Level,
                picks = r.Picks.Select(p => new { cell = p.Cell, bee = p.Bee }).ToList(),
                opened = r.Info.Order.Select(c => new { cell = c, clue = r.Info.Clues[c] }).ToList(),
                r.Multiplier,
                r.Payout,
                r.Status,
                beeCells = r.Live ? null : r.BeeCells.Order().ToList(),
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
                        // Before the first pick this returns the stake (Round.Multiplier is ×1 then).
                        await SettleAsync(seat, round, "cashed");
                        logger.LogInformation("Hive round {RoundId} for {PlayerId} was idle — cashed out at ×{Multiplier}", round.RoundId, seat.Player.PlayerId, round.Multiplier);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Hive idle settle failed for {PlayerId}", seat.Player.PlayerId); }
                    finally { seat.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
