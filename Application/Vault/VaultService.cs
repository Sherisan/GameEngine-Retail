using System.Collections.Concurrent;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Blackjack;
using Worldiator.Server.Domain.Vault;

namespace Worldiator.Server.Application.Vault;

/// <summary>
/// VAULT CRACKER (Engine = Vault): one vault per player and game at a time. See VaultRules for the math.
///
/// Bet: the stake is debited ("{round}-bet") and the alarm tumbler of every ring is drawn from the player's committed
/// server seed (its hash was shown before the bet) and their client seed. Pick: a safe tumbler cracks the digit, the
/// alarm tumbler ends the round with nothing paid. Intel (the Stethoscope): debits its price ("{round}-intel") and cracks
/// the current digit with a safe tumbler. Cash out (or opening the vault) credits the payout once ("{round}-win").
/// The server seed is revealed when the round ends.
///
/// Rounds live in memory (single node), like blackjack and DEFUSE. A round left unfinished for 10 minutes is cashed out
/// at the rung it reached; at rung 0 that returns the stake.
/// </summary>
public sealed class VaultService(IServiceScopeFactory scopes, ILogger<VaultService> logger) : BackgroundService
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
        public required int[] Alarms { get; init; }
        public decimal IntelCost { get; set; }
        public List<int> Picks { get; } = [];
        public int? IntelStep { get; set; }
        public string Status { get; set; } = "live";
        public decimal Payout { get; set; }
        public decimal? Balance { get; set; }
        public bool WinPending { get; set; }
        public DateTime LastAt { get; set; } = DateTime.UtcNow;

        public bool Live => Status == "live";
        /// <summary>Digits cracked. The pick that tripped the alarm doesn't count.</summary>
        public int Rung => Status == "alarm" ? Picks.Count - 1 : Picks.Count;
    }

    private static string Key(string gameId, string playerId) => $"{gameId.ToLowerInvariant()}|{playerId}";

    private Seat SeatFor(GameDefinition game, PlayerSession player) =>
        _seats.GetOrAdd(Key(game.GameId, player.PlayerId), _ => new Seat { Player = player });

    // ── Public API (VaultController) ──────────────────────────────────────

    public Task<(object? State, WalletResult? Error)> StateAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, _ => Task.FromResult<WalletResult?>(null));

    public Task<(object? State, WalletResult? Error)> BetAsync(GameDefinition game, PlayerSession player, decimal stake) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is { Live: true }) return Bad("ROUND_IN_PLAY", "Finish the vault in play first.");
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
                    Alarms = VaultFair.Alarms(seat.NextServerSeed, seat.ClientSeed, seat.Nonce),
                };
            }
            seat.NextServerSeed = Shoe.NewSeed();
            logger.LogInformation("Vault bet: game={GameId} player={PlayerId} round={RoundId} stake={Stake} seedHash={Hash} nonce={Nonce}",
                game.GameId, player.PlayerId, roundId, stake, Shoe.Hash(seat.Round.ServerSeed), seat.Round.Nonce);
            return null;
        });

    public Task<(object? State, WalletResult? Error)> PickAsync(GameDefinition game, PlayerSession player, int tumbler) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no vault in play — bet first.");
            if (tumbler is < 0 or >= VaultRules.Tumblers) return Bad("INVALID_TUMBLER", "Unknown tumbler.");
            round.LastAt = DateTime.UtcNow;
            var step = round.Picks.Count;
            round.Picks.Add(tumbler);
            if (tumbler == round.Alarms[step]) await SettleAsync(seat, round, "alarm");
            else if (round.Picks.Count == VaultRules.Steps) await SettleAsync(seat, round, "opened");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> IntelAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no vault in play — bet first.");
            if (round.IntelStep != null) return Bad("NOT_ALLOWED", "The Stethoscope was already used this round.");
            round.LastAt = DateTime.UtcNow;
            var step = round.Picks.Count;
            var cost = VaultRules.IntelCost(round.Bet, step);
            using (var scope = scopes.CreateScope())
            {
                var debit = await scope.ServiceProvider.GetRequiredService<WalletService>()
                    .DebitAsync(game, player, round.RoundId, $"{round.RoundId}-intel", cost, extra: true);
                if (!debit.Ok) return debit;
                round.Balance = debit.Balance;
            }
            var safe = Enumerable.Range(0, VaultRules.Tumblers).Where(t => t != round.Alarms[step]).ToList();
            round.IntelCost = cost;
            round.IntelStep = step;
            round.Picks.Add(VaultFair.Intel(round.ServerSeed, round.ClientSeed, round.Nonce, safe));
            if (round.Picks.Count == VaultRules.Steps) await SettleAsync(seat, round, "opened");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> CashOutAsync(GameDefinition game, PlayerSession player) =>
        WithSeat(game, player, async seat =>
        {
            if (seat.Round is not { Live: true } round) return Bad("NO_ROUND", "There is no vault in play — bet first.");
            if (round.Rung == 0) return Bad("NOT_ALLOWED", "Crack at least one digit first.");
            await SettleAsync(seat, round, "cashed");
            return null;
        });

    public Task<(object? State, WalletResult? Error)> SetClientSeedAsync(GameDefinition game, PlayerSession player, string? clientSeed) =>
        WithSeat(game, player, seat =>
        {
            var clean = clientSeed?.Trim() ?? "";
            if (seat.Round is { Live: true }) return Task.FromResult(Bad("ROUND_IN_PLAY", "Change seeds between rounds."));
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
        round.Payout = status == "alarm" ? 0m : Math.Min(VaultRules.Payout(round.Bet, round.Rung), round.Game.MaxWin);
        var player = seat.Player;

        if (round.Payout > 0)
        {
            using var scope = scopes.CreateScope();
            var credit = await scope.ServiceProvider.GetRequiredService<WalletService>().CreditAsync(round.Game, player, round.RoundId, $"{round.RoundId}-win", round.Payout);
            if (credit.Ok) round.Balance = credit.Balance;
            else
            {
                round.WinPending = true;
                logger.LogCritical("Vault win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    round.Game.GameId, player.PlayerId, round.RoundId, round.Payout, credit.ErrorCode, credit.Error);
            }
        }

        seat.History.Insert(0, new
        {
            id = round.RoundId,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            bet = round.Bet,
            intelCost = round.IntelCost,
            picks = round.Picks.ToList(),
            intelStep = round.IntelStep,
            rung = round.Rung,
            payout = round.Payout,
            status,
            alarms = round.Alarms,
            serverSeed = round.ServerSeed,
            serverSeedHash = Shoe.Hash(round.ServerSeed),
            clientSeed = round.ClientSeed,
            nonce = round.Nonce,
        });
        if (seat.History.Count > HistoryLimit) seat.History.RemoveAt(seat.History.Count - 1);

        logger.LogInformation("Vault settle: game={GameId} player={PlayerId} round={RoundId} status={Status} stake={Stake} intel={Intel} win={Win} picks={Picks} alarms={Alarms}",
            round.Game.GameId, player.PlayerId, round.RoundId, status, round.Bet, round.IntelCost, round.Payout,
            string.Join(',', round.Picks), string.Join(',', round.Alarms));
    }

    // ── What the dial shows ───────────────────────────────────────────────

    private static object Dto(Seat seat)
    {
        var r = seat.Round;
        return new
        {
            round = r == null ? null : new
            {
                id = r.RoundId,
                r.Bet,
                r.IntelCost,
                picks = r.Picks,
                intelStep = r.IntelStep,
                rung = r.Rung,
                r.Payout,
                r.Status,
                alarms = r.Live ? null : r.Alarms,
                r.Balance,
                r.WinPending,
            },
            fairness = new { serverSeedHash = Shoe.Hash(seat.NextServerSeed), clientSeed = seat.ClientSeed, nonce = seat.Nonce + 1 },
            history = seat.History,
        };
    }

    private static WalletResult Bad(string code, string message) => WalletResult.Fail(WalletErrorKind.BadRequest, code, message);

    // ── Abandoned rounds ──────────────────────────────────────────────────

    /// <summary>Every minute: rounds idle for 10 minutes are cashed out at their current rung, so a stake is never left hanging.</summary>
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
                        // Multiplier(0) is 1, so an untouched vault pays the stake back.
                        await SettleAsync(seat, round, "cashed");
                        logger.LogInformation("Vault round {RoundId} for {PlayerId} was idle — settled at rung {Rung}", round.RoundId, seat.Player.PlayerId, round.Rung);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Vault idle settle failed for {PlayerId}", seat.Player.PlayerId); }
                    finally { seat.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
