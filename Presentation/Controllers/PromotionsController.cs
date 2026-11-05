using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Promotions;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Promotions for games that don't use the /promohub SignalR hub (the card games, roulette, blackjack): the same state
/// as PromoHub.GetState — live promotions for the token's game with the player's progress, their free bets, and what
/// they won lately (recentAwards) — read after each play. Wins from a play also come back in the play's own response.
/// </summary>
[ApiController]
[Route("api/promotions")]
[Authorize]
public class PromotionsController(PromoEngine promos) : ControllerBase
{
    [HttpGet("state")]
    public async Task<IActionResult> State()
    {
        var player = SessionTokenService.FromPrincipal(User);
        if (player == null) return Unauthorized();
        return Ok(await promos.StateForAsync(player));
    }
}
