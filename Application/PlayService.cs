using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

/// <summary>FreeBet: play one of the player's promo free cards instead (its value is the stake; Stake is ignored).</summary>
public record PlayRequest(decimal Stake, string? Pick = null, decimal? Target = null, bool FreeBet = false);

public record PlayResult(
    string RoundId,
    decimal Stake,
    decimal Win,
    /// <summary>Payout multiplier applied to the stake (0 = loss).</summary>
    decimal Multiplier,
    /// <summary>Pick model: the option the RNG drew.</summary>
    string? Outcome,
    /// <summary>Target model: the crash point the RNG drew.</summary>
    decimal? CrashPoint,
    decimal? Balance,
    RngProof Proof,
    /// <summary>True if the win was decided but the wallet credit failed — it's in the ledger as Failed for reprocessing.</summary>
    bool WinPending,
    /// <summary>Scratch games: the card shown to the player, built from the drawn prize (see RngSettings.Card).</summary>
    object? Card = null,
    /// <summary>Played with a promo free card: Win is what the promotion paid.</summary>
    bool FreeBet = false,
    /// <summary>What this play earned from promotions (mystery prizes, collection symbols, buy-X progress, …).</summary>
    IReadOnlyList<PromoOutcome>? Promos = null);

/// <summary>
/// One play of a generic RNG game (see RngSettings): debit the stake, draw the outcome, credit the win.
/// The outcome and the win are decided here — the frontend only sends the stake and the player's choice.
/// Each play is its own round; wallet transaction ids are "{roundId}-bet" and "{roundId}-win".
/// </summary>
public class PlayService(WalletService wallet, IGameRngService rng, PromoEngine promos, ILogger<PlayService> logger)
{
    public async Task<(PlayResult? Result, WalletResult? Error)> PlayAsync(GameDefinition game, PlayerSession player, PlayRequest req)
    {
        var settings = game.Rng;
        if (settings == null)
            return (null, WalletResult.Fail(WalletErrorKind.BadRequest, "NOT_PLAYABLE", "This game doesn't play through this API's RNG."));

        RngOption? picked = null;
        switch (settings.Model)
        {
            case RngModel.Pick:
                picked = settings.Options.FirstOrDefault(o => string.Equals(o.Key, req.Pick, StringComparison.OrdinalIgnoreCase));
                if (picked == null)
                    return (null, WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_PICK",
                        "pick must be one of: " + string.Join(", ", settings.Options.Select(o => o.Key))));
                break;
            case RngModel.Target:
                if (req.Target is not { } t || t < 1.01m || t > settings.MaxMultiplier || t != Math.Round(t, 2))
                    return (null, WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_TARGET",
                        FormattableString.Invariant($"target must be 1.01-{settings.MaxMultiplier} with at most 2 decimals.")));
                break;
        }

        // A free card's value is its stake; it must still be one of the card prices, so the card can be drawn for it.
        var stake = req.Stake;
        Domain.Promotions.PromoAward? free = null;
        if (req.FreeBet)
        {
            free = await promos.ClaimFreeBetAsync(player, game.GameId);
            if (free == null)
                return (null, WalletResult.Fail(WalletErrorKind.BadRequest, "NO_FREE_BET", "You have no free card for this game."));
            stake = free.Amount;
        }

        if (settings.Stakes.Length > 0 && !settings.Stakes.Contains(stake))
        {
            if (free != null) await promos.ReleaseFreeBetAsync(free);
            return (null, WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_STAKE",
                (free != null ? "This free card's value isn't one of the card prices. " : "") +
                "Stake must be one of: " + string.Join(", ", settings.Stakes.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }

        var roundId = Guid.NewGuid().ToString("N");
        decimal? balance = null;
        if (free == null)
        {
            var debit = await wallet.DebitAsync(game, player, roundId, $"{roundId}-bet", stake);
            if (!debit.Ok) return (null, debit);
            balance = debit.Balance;
        }

        // Drawn only after the stake is taken, so a failed debit can't be used to peek at outcomes.
        var draw = rng.Draw(game);
        decimal multiplier;
        string? outcome = null;
        decimal? crashPoint = null;
        switch (settings.Model)
        {
            case RngModel.Pick:
                var drawn = settings.Options[(int)draw.Value];
                outcome    = drawn.Key;
                multiplier = drawn == picked ? drawn.Payout : 0m;
                break;
            case RngModel.Target:
                crashPoint = draw.Value;
                multiplier = draw.Value >= req.Target!.Value ? req.Target.Value : 0m;
                break;
            default:
                multiplier = settings.Outcomes[(int)draw.Value].Multiplier;
                break;
        }

        var win = Math.Min(Math.Round(stake * multiplier, 2, MidpointRounding.ToZero), game.MaxWin);
        // The card is built from the drawn prize and checks itself: it always pays exactly stake × multiplier.
        var card = settings.Card != null ? CardLayouts.Build(settings.Card, stake, multiplier, settings) : null;
        var winPending = false;
        IReadOnlyList<PromoOutcome> promoOutcomes = [];
        if (free != null)
        {
            // The promotion pays a free card's prize (or what's above its price, as the promotion says).
            win = await promos.SettleFreeBetAsync(free, roundId, win);
            balance = await TryBalanceAsync(player);
        }
        else if (win > 0)
        {
            var credit = await wallet.CreditAsync(game, player, roundId, $"{roundId}-win", win);
            if (credit.Ok)
                balance = credit.Balance;
            else
            {
                winPending = true;
                logger.LogCritical("Play win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    game.GameId, player.PlayerId, roundId, win, credit.ErrorCode, credit.Error);
            }
        }

        logger.LogInformation(
            "Play: game={GameId} player={PlayerId} round={RoundId} stake={Stake} pick={Pick} target={Target} draw={Draw} win={Win} seedHash={Hash} nonce={Nonce} card={Card}",
            game.GameId, player.PlayerId, roundId, stake, req.Pick, req.Target, draw.Value, win, draw.Proof.ServerSeedHash, draw.Proof.Nonce,
            card == null ? null : System.Text.Json.JsonSerializer.Serialize(card));

        if (free == null)
        {
            // A "hit" is the card's top prize (Multiplier model) or a winning pick.
            var hit = settings.Model switch
            {
                RngModel.Multiplier => multiplier > 0 && multiplier == settings.Outcomes.Max(o => o.Multiplier),
                RngModel.Pick => multiplier > 0,
                _ => false,
            };
            promoOutcomes = await promos.PlayedAsync(new PlayReport(game.GameId, player, roundId, stake, winPending ? 0 : win) { Hits = hit ? 1 : 0 });
            if (promoOutcomes.Any(o => o.Kind == "Cash")) balance = await TryBalanceAsync(player) ?? balance;
        }

        return (new PlayResult(roundId, stake, win, multiplier, outcome, crashPoint, balance, draw.Proof, winPending, card, free != null, promoOutcomes), null);
    }

    private async Task<decimal?> TryBalanceAsync(PlayerSession player)
    {
        try { return await wallet.GetBalanceAsync(player); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Balance lookup after play failed for {PlayerId}", player.PlayerId);
            return null;
        }
    }
}
