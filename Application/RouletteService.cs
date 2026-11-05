using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Roulette;

namespace Worldiator.Server.Application;

public record RouletteBet(string Type, int[] Numbers, decimal Amount);

/// <summary>FreeChip: a position to put one of the player's promo free chips on (its value is the chip's; Amount is ignored).</summary>
public record RouletteSpinRequest(List<RouletteBet>? Bets, RouletteBet? FreeChip = null);

/// <summary>Free: the promo free chip — Win is what the promotion paid for it.</summary>
public record RouletteBetResult(string Type, int[] Numbers, decimal Amount, decimal Win, bool Free = false);

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
    bool WinPending,
    /// <summary>What this spin earned from promotions (lucky number, boosted payout, streaks, …).</summary>
    IReadOnlyList<PromoOutcome>? Promos = null);

/// <summary>
/// One on-demand roulette spin: validate every bet against the table, debit the total stake once, draw the
/// pocket, credit the total win once. The frontend only sends the bets; the number and the win are decided here.
/// Wallet transaction ids are "{roundId}-bet" and "{roundId}-win", as for api/play.
/// </summary>
public class RouletteService(WalletService wallet, IGameRngService rng, PromoEngine promos, ILogger<RouletteService> logger)
{
    public async Task<(RouletteSpinResult? Result, WalletResult? Error)> SpinAsync(GameDefinition game, PlayerSession player, RouletteSpinRequest? req)
    {
        static (RouletteSpinResult?, WalletResult?) Bad(string code, string message) =>
            (null, WalletResult.Fail(WalletErrorKind.BadRequest, code, message));

        var settings = game.Roulette ?? new RouletteSettings();
        var bets = req?.Bets ?? [];
        var chip = req?.FreeChip;
        if (bets.Count == 0 && chip == null) return Bad("NO_BETS", "Place at least one bet.");
        if (bets.Count > settings.MaxBetsPerSpin) return Bad("TOO_MANY_BETS", $"At most {settings.MaxBetsPerSpin} bets per spin.");
        if (chip != null && (chip.Numbers == null || string.IsNullOrWhiteSpace(chip.Type) || !RouletteTable.IsValid(chip.Type, chip.Numbers)))
            return Bad("INVALID_BET", "The free chip isn't on a bet on this table.");

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

        // The free chip: claimed first; it may be limited to some bet types by its promotion.
        Domain.Promotions.PromoAward? free = null;
        string? chipType = null;
        if (chip != null)
        {
            free = await promos.ClaimFreeBetAsync(player, game.GameId);
            if (free == null) return Bad("NO_FREE_BET", "You have no free chip for this table.");
            chipType = RouletteTable.Canonical(chip.Type)!;
            var allowed = promos.FreeBetTypesFor(free);
            if (allowed.Count > 0 && !allowed.Contains(chipType, StringComparer.OrdinalIgnoreCase))
            {
                await promos.ReleaseFreeBetAsync(free);
                return Bad("FREE_CHIP_NOT_ALLOWED", $"This free chip can only go on: {string.Join(", ", allowed)}.");
            }
        }

        // Total against MinBet / MaxBet is checked by the wallet debit. A spin with only the free chip debits nothing.
        var stake   = positions.Values.Sum(p => p.Amount);
        var roundId = Guid.NewGuid().ToString("N");
        decimal? balance = null;
        if (stake > 0)
        {
            var debit = await wallet.DebitAsync(game, player, roundId, $"{roundId}-bet", stake);
            if (!debit.Ok)
            {
                if (free != null) await promos.ReleaseFreeBetAsync(free);
                return (null, debit);
            }
            balance = debit.Balance;
        }

        // Drawn only after the stake is taken, so a failed debit can't be used to peek at outcomes.
        var draw   = rng.Draw(game);
        var number = (int)draw.Value;
        var results = positions.Values
            .Select(p => new RouletteBetResult(p.Type, p.Numbers, p.Amount,
                p.Numbers.Contains(number) ? p.Amount * (RouletteTable.Payouts[p.Type] + 1) : 0m))
            .ToList();
        var win = Math.Min(results.Sum(r => r.Win), game.MaxWin);

        var winPending = false;
        if (free != null)
        {
            var chipNumbers = chip!.Numbers.Order().ToArray();
            var chipReturn = chipNumbers.Contains(number) ? free.Amount * (RouletteTable.Payouts[chipType!] + 1) : 0m;
            var chipPaid = await promos.SettleFreeBetAsync(free, roundId, chipReturn);
            results.Add(new RouletteBetResult(chipType!, chipNumbers, free.Amount, chipPaid, Free: true));
        }

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

        // Promotions see the real-money part of the spin; promo payouts (free chip, boosts, lucky number) change the balance.
        var promoOutcomes = await promos.PlayedAsync(new PlayReport(game.GameId, player, roundId, stake, winPending ? 0 : win)
        {
            Number = number,
            StraightStakes = positions.Values.Where(p => p.Type == "straight").ToDictionary(p => p.Numbers[0], p => p.Amount),
            Hits = positions.Values.Any(p => p.Type == "straight" && p.Numbers[0] == number) ? 1 : 0,
        });
        if (free != null || promoOutcomes.Any(o => o.Kind == "Cash") || balance == null)
            balance = await TryBalanceAsync(player) ?? balance;

        var total = win + results.Where(r => r.Free).Sum(r => r.Win);
        return (new RouletteSpinResult(roundId, number, RouletteTable.ColorOf(number), stake, total, balance, results, draw.Proof, winPending, promoOutcomes), null);
    }

    private async Task<decimal?> TryBalanceAsync(PlayerSession player)
    {
        try { return await wallet.GetBalanceAsync(player); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Balance lookup after spin failed for {PlayerId}", player.PlayerId);
            return null;
        }
    }
}
