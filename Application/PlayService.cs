using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

public record PlayRequest(decimal Stake, string? Pick = null, decimal? Target = null);

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
    object? Card = null);

/// <summary>
/// One play of a generic RNG game (see RngSettings): debit the stake, draw the outcome, credit the win.
/// The outcome and the win are decided here — the frontend only sends the stake and the player's choice.
/// Each play is its own round; wallet transaction ids are "{roundId}-bet" and "{roundId}-win".
/// </summary>
public class PlayService(WalletService wallet, IGameRngService rng, ILogger<PlayService> logger)
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

        if (settings.Stakes.Length > 0 && !settings.Stakes.Contains(req.Stake))
            return (null, WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_STAKE",
                "Stake must be one of: " + string.Join(", ", settings.Stakes.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        var roundId = Guid.NewGuid().ToString("N");
        var debit = await wallet.DebitAsync(game, player, roundId, $"{roundId}-bet", req.Stake);
        if (!debit.Ok) return (null, debit);

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

        var win = Math.Min(Math.Round(req.Stake * multiplier, 2, MidpointRounding.ToZero), game.MaxWin);
        // The card is built from the drawn prize and checks itself: it always pays exactly stake × multiplier.
        var card = settings.Card != null ? CardLayouts.Build(settings.Card, req.Stake, multiplier, settings) : null;
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
                logger.LogCritical("Play win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    game.GameId, player.PlayerId, roundId, win, credit.ErrorCode, credit.Error);
            }
        }

        logger.LogInformation(
            "Play: game={GameId} player={PlayerId} round={RoundId} stake={Stake} pick={Pick} target={Target} draw={Draw} win={Win} seedHash={Hash} nonce={Nonce} card={Card}",
            game.GameId, player.PlayerId, roundId, req.Stake, req.Pick, req.Target, draw.Value, win, draw.Proof.ServerSeedHash, draw.Proof.Nonce,
            card == null ? null : System.Text.Json.JsonSerializer.Serialize(card));

        return (new PlayResult(roundId, req.Stake, win, multiplier, outcome, crashPoint, balance, draw.Proof, winPending, card), null);
    }
}
