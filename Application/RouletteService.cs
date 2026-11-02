using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Roulette;

namespace Worldiator.Server.Application;

public record RouletteBet(string Type, int[] Numbers, decimal Amount);

public record RouletteSpinRequest(List<RouletteBet> Bets);

public record RouletteBetResult(string Type, int[] Numbers, decimal Amount, decimal Win);

public record RouletteSpinResult(
    string RoundId,
    /// <summary>The winning pocket, 0-36.</summary>
    int Number,
    string Color,
    decimal Stake,
    decimal Win,
    decimal? Balance,
    /// <summary>Every position played (same positions merged), with what each paid.</summary>
    IReadOnlyList<RouletteBetResult> Bets,
    RngProof Proof,
    /// <summary>True if the win was decided but the wallet credit failed — it's in the ledger as Failed for reprocessing.</summary>
    bool WinPending);

/// <summary>
/// One on-demand roulette spin: validate every bet against the table, debit the total stake once, draw the
/// pocket, credit the total win once. The frontend only sends the bets; the number and the win are decided here.
/// Wallet transaction ids are "{roundId}-bet" and "{roundId}-win", as for api/play.
/// </summary>
public class RouletteService(WalletService wallet, IGameRngService rng, ILogger<RouletteService> logger)
{
    public async Task<(RouletteSpinResult? Result, WalletResult? Error)> SpinAsync(GameDefinition game, PlayerSession player, RouletteSpinRequest? req)
    {
        static (RouletteSpinResult?, WalletResult?) Bad(string code, string message) =>
            (null, WalletResult.Fail(WalletErrorKind.BadRequest, code, message));

        var settings = game.Roulette ?? new RouletteSettings();
        if (req?.Bets is not { Count: > 0 } bets) return Bad("NO_BETS", "Place at least one bet.");
        if (bets.Count > settings.MaxBetsPerSpin) return Bad("TOO_MANY_BETS", $"At most {settings.MaxBetsPerSpin} bets per spin.");

        // Same position twice (e.g. a call bet over a chip already there) is one bet.
        var positions = new Dictionary<string, (string Type, int[] Numbers, decimal Amount)>();
        foreach (var b in bets)
        {
            if (b?.Numbers == null || string.IsNullOrWhiteSpace(b.Type) || !RouletteTable.IsValid(b.Type, b.Numbers))
                return Bad("INVALID_BET", $"\"{b?.Type} {string.Join(",", b?.Numbers ?? [])}\" is not a bet on this table.");
            if (b.Amount <= 0 || b.Amount != Math.Round(b.Amount, 2))
                return Bad("INVALID_AMOUNT", "Each bet must be a positive amount with at most 2 decimals.");

            var type = RouletteTable.Canonical(b.Type)!;
            var key  = RouletteTable.Key(type, b.Numbers);
            var prev = positions.TryGetValue(key, out var p) ? p.Amount : 0m;
            positions[key] = (type, b.Numbers.Order().ToArray(), prev + b.Amount);
        }
        foreach (var (type, numbers, amount) in positions.Values)
        {
            var limit = settings.LimitFor(type, game.MaxBet);
            if (amount > limit)
                return Bad("OVER_LIMIT", FormattableString.Invariant($"The table limit for a {type} bet is {limit} (on {string.Join("-", numbers)})."));
        }

        // Total against MinBet / MaxBet is checked by the wallet debit.
        var stake   = positions.Values.Sum(p => p.Amount);
        var roundId = Guid.NewGuid().ToString("N");
        var debit   = await wallet.DebitAsync(game, player, roundId, $"{roundId}-bet", stake);
        if (!debit.Ok) return (null, debit);

        // Drawn only after the stake is taken, so a failed debit can't be used to peek at outcomes.
        var draw   = rng.Draw(game);
        var number = (int)draw.Value;
        var results = positions.Values
            .Select(p => new RouletteBetResult(p.Type, p.Numbers, p.Amount,
                p.Numbers.Contains(number) ? p.Amount * (RouletteTable.Payouts[p.Type] + 1) : 0m))
            .ToList();
        var win = Math.Min(results.Sum(r => r.Win), game.MaxWin);

        var balance = debit.Balance;
        var winPending = false;
        if (win > 0)
        {
            var credit = await wallet.CreditAsync(game, player, roundId, $"{roundId}-win", win);
            if (credit.Ok)
                balance = credit.Balance;
            else
            {
                winPending = true;
                logger.LogCritical("Roulette win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    game.GameId, player.PlayerId, roundId, win, credit.ErrorCode, credit.Error);
            }
        }

        logger.LogInformation(
            "Roulette: game={GameId} player={PlayerId} round={RoundId} stake={Stake} positions={Positions} number={Number} win={Win} seedHash={Hash} nonce={Nonce}",
            game.GameId, player.PlayerId, roundId, stake, positions.Count, number, win, draw.Proof.ServerSeedHash, draw.Proof.Nonce);

        return (new RouletteSpinResult(roundId, number, RouletteTable.ColorOf(number), stake, win, balance, results, draw.Proof, winPending), null);
    }
}
