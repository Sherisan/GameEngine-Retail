using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Recovery;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Connection-loss safety. The game calls this when it starts and whenever its connection to the server comes back
/// after a drop; until it gets an answer it shows "Connecting to server…" and can't be played.
///
///   POST api/session/recover → { refunds: [{ gameId, roundId, kind, stake, amount, freeBet, slot }], balance, currency }
///
/// Crash games on /gamehub (Feeding Frenzy): a bet still open in the round (waiting, or flying before the crash point)
/// is refunded. The hub already refunds when it loses the player's last connection; this catches a drop the client
/// noticed first, and reports those earlier refunds too. kind is always "refunded" here (amount = the stake).
/// </summary>
[ApiController]
[Route("api/session")]
[Authorize]
public class SessionController(
    IGameRegistry games,
    ConnectionRecovery recovery,
    WalletService wallet,
    GameEngine crash,
    ILogger<SessionController> logger) : ControllerBase
{
    [HttpPost("recover")]
    public async Task<IActionResult> Recover()
    {
        var player = SessionTokenService.FromPrincipal(User);
        if (player == null) return Unauthorized();
        var game = games.Find(player.GameId);
        var refunds = new List<RecoveredBet>();

        var engine = game?.Engine ?? (player.GameId == SessionTokenService.DefaultGameId ? EngineKind.Custom : (EngineKind?)null);
        if (engine == EngineKind.Custom)
            refunds.AddRange(await crash.RefundOpenBetsAsync(player.PlayerId, player.GameId));

        // Refunds the hub made when it lost the connection, before the player came back.
        var earlier = recovery.Drain(player.GameId, player.PlayerId);
        refunds.InsertRange(0, earlier.Where(n => !refunds.Any(r => r.RoundId == n.RoundId && r.Slot == n.Slot)));

        decimal? balance = null;
        try { balance = await wallet.GetBalanceAsync(player); }
        catch (Exception ex) { logger.LogWarning(ex, "Balance lookup after recovery failed for {PlayerId}", player.PlayerId); }

        if (refunds.Count > 0)
            logger.LogInformation("Session recovered: game={GameId} player={PlayerId} {Refunds}", player.GameId, player.PlayerId,
                string.Join("; ", refunds.Select(r => $"{r.Kind} {r.Amount} (stake {r.Stake}, round {r.RoundId})")));

        return Ok(new
        {
            refunds = refunds.Select(r => new { r.GameId, r.RoundId, r.Kind, r.Stake, r.Amount, r.FreeBet, r.Slot }),
            balance,
            currency = player.Currency,
        });
    }
}
