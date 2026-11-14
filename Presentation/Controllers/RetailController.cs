using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Retail;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Retail;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Retail (land-based cabinet) endpoints. Only answer when RGS:Provider = Retail. The EGM holds the balance
/// (see RetailRGSAdapter for the calls this API makes to it); these are the calls made to this API:
///   POST api/retail/session   — the game, launched as ?egmid=EGM1, swaps the EGM id for a session token
///   POST api/retail/terminal  — the EGM's Bill_Inserted / AFT_Deposit / Cashout / Balance events
/// Contract for both sides: docs/RETAIL-TERMINAL-INTEGRATION.md.
/// </summary>
[ApiController]
[Route("api/retail")]
[AllowAnonymous]
public class RetailController(
    RetailWalletService retail,
    IRGSAdapter wallet,
    SessionTokenService tokens,
    IGameRegistry games,
    GameEngine engine,
    ILogger<RetailController> logger) : ControllerBase
{
    // ─── Game launch ──────────────────────────────────────────────────────────

    public record SessionRequest(string? EgmId, string? GameId = null);

    /// <summary>The game was opened as ?egmid=…: return a session token for that cabinet, and its balance.</summary>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession([FromBody] SessionRequest body)
    {
        if (!retail.Enabled) return NotFound();
        var egm = retail.FindEgm(body.EgmId);
        if (egm == null) return Error(404, "EGM_NOT_FOUND", $"EGM \"{body.EgmId}\" is not registered or is disabled.");
        if (!RetailWalletService.IsAllowedClient(egm, HttpContext.Connection.RemoteIpAddress))
        {
            logger.LogWarning("Retail session for {EgmId} refused from {Ip} — not one of its AllowedIps",
                egm.EgmId, HttpContext.Connection.RemoteIpAddress);
            return Error(403, "EGM_ADDRESS_NOT_ALLOWED", "This EGM can't be played from this device.");
        }

        var gameId = string.IsNullOrWhiteSpace(body.GameId) ? retail.Options.GameId : body.GameId.Trim();
        if (gameId != SessionTokenService.DefaultGameId && games.Find(gameId) == null)
            return Error(404, "GAME_NOT_FOUND", $"Unknown or disabled game \"{gameId}\".");

        var currency = retail.CurrencyOf(egm);
        return Ok(new
        {
            token   = tokens.Issue(egm.EgmId, egm.Name ?? egm.EgmId, currency, gameId),
            egmId   = egm.EgmId,
            name    = egm.Name ?? egm.EgmId,
            currency,
            // Null when the EGM wallet can't be reached right now — the game reads it again when it connects.
            balance = await TryReadBalanceAsync(egm),
            gameId,
        });
    }

    // ─── Terminal events ──────────────────────────────────────────────────────

    public record TerminalRequest(string? EgmId, string? Event, decimal Amount = 0, string? TransactionId = null, decimal? Balance = null);

    public record TerminalResponse(string EgmId, string Event, string? TransactionId, decimal Amount, decimal Balance,
        string Currency, DateTime Timestamp);

    /// <summary>
    /// What the EGM tells this API, authenticated by X-Terminal-Key:
    ///   Bill_Inserted / AFT_Deposit — money arrived on the EGM's meter: show the new balance in the game.
    ///   Cashout — the player wants to cash out: refused (409) while a bet is in play, otherwise answered with the
    ///             game's balance, the amount the EGM pays out.
    ///   Balance — the balance as the game sees it.
    /// </summary>
    [HttpPost("terminal")]
    public async Task<IActionResult> Terminal([FromBody] TerminalRequest body)
    {
        if (!retail.Enabled) return NotFound();
        var egm = retail.FindEgm(body.EgmId);
        if (egm == null) return Error(404, "EGM_NOT_FOUND", $"EGM \"{body.EgmId}\" is not registered or is disabled.");
        if (!IsTerminal(egm)) return Error(401, "UNAUTHORIZED", "Missing or wrong X-Terminal-Key.");

        var ev = Normalise(body.Event);
        if (ev is not ("billinserted" or "aftdeposit" or "cashout" or "balance"))
            return Error(400, "INVALID_EVENT", "event must be Bill_Inserted, AFT_Deposit, Cashout or Balance.");
        if (ev != "balance" && (string.IsNullOrWhiteSpace(body.TransactionId) || body.TransactionId.Length > 100))
            return Error(400, "INVALID_REFERENCE", "transactionId is required (max 100 characters).");
        if (ev is "billinserted" or "aftdeposit" && (body.Amount <= 0 || decimal.Round(body.Amount, 2) != body.Amount))
            return Error(400, "INVALID_AMOUNT", "amount must be more than 0, with at most 2 decimal places.");
        if (body.Balance is < 0)
            return Error(400, "INVALID_AMOUNT", "balance can't be negative.");

        // A cash-out waits for the round: a stake still riding would be settled after the payout.
        if (ev == "cashout" && HasBetInPlay(egm.EgmId))
            return Error(409, "ROUND_IN_PROGRESS", "A bet is in play on this EGM — retry the cash-out when the round ends.");

        // The EGM's meter is the truth. Deposits may carry the new balance (saves a round trip); otherwise read it.
        decimal balance;
        if (ev is "billinserted" or "aftdeposit" && body.Balance is { } sent)
            balance = sent;
        else
        {
            var read = await TryReadBalanceAsync(egm);
            if (read == null) return Error(502, "EGM_WALLET_UNAVAILABLE", "The EGM wallet endpoint didn't answer — retry.");
            balance = read.Value;
        }

        var name = ev switch { "billinserted" => "Bill_Inserted", "aftdeposit" => "AFT_Deposit", "cashout" => "Cashout", _ => "Balance" };
        logger.LogInformation("Retail {Event} on {EgmId} (ref {Reference}, amount {Amount}) → balance {Balance}",
            name, egm.EgmId, body.TransactionId, body.Amount, balance);

        switch (ev)
        {
            case "billinserted" or "aftdeposit":
                await retail.PushBalanceAsync(egm.EgmId, balance, name == "Bill_Inserted" ? "BillInserted" : "AftDeposit");
                break;
            case "cashout":
                // The EGM pays out and empties its meter now; the game shows the empty meter straight away.
                await retail.PushBalanceAsync(egm.EgmId, 0, "Cashout");
                break;
        }

        return Ok(new TerminalResponse(egm.EgmId, name, body.TransactionId,
            ev is "billinserted" or "aftdeposit" ? body.Amount : ev == "cashout" ? balance : 0,
            balance, retail.CurrencyOf(egm), DateTime.UtcNow));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<decimal?> TryReadBalanceAsync(RetailEgm egm)
    {
        try { return await wallet.GetBalanceAsync(egm.EgmId, retail.CurrencyOf(egm)); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EGM wallet balance read failed for {EgmId}", egm.EgmId);
            return null;
        }
    }

    private bool IsTerminal(RetailEgm egm) =>
        retail.IsValidTerminalKey(egm, Request.Headers[RetailWalletService.TerminalKeyHeader].FirstOrDefault());

    // A Feeding Frenzy stake is riding from the moment it is placed until it is cashed out or the round crashes.
    private bool HasBetInPlay(string egmId) =>
        engine.CurrentRound is { Phase: not GamePhase.Crashed }
        && engine.GetCurrentBets().Any(b => string.Equals(b.PlayerId, egmId, StringComparison.OrdinalIgnoreCase) && b.CashedOutAt == null);

    private static string Normalise(string? value) =>
        (value ?? string.Empty).Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();

    private ObjectResult Error(int status, string code, string message) => StatusCode(status, new { error = code, message });
}
