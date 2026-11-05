using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Promotions;

namespace Worldiator.Server.Presentation.Hubs;

/// <summary>
/// Promotions for the game the player's session token is for — every crash frontend connects here alongside its
/// own game hub, with the same token.
///   GetState()      live promotions for this game (leaderboard standings, race places left, …) and the player's free bets
/// Server → client:
///   "PromoAward"    { awardId, promotion, type, kind: Cash | FreeBet, amount, reason, balance? }  to the winner only
///   "PromoEvent"    { promotionId, name, type, message, amount, winners }                  to everyone in the game
///   "PromoChanged"  (no payload) — the player's free bets changed; call GetState again
/// Free bets are played through the game's own hub (GameHub.PlaceFreeBet, LiveCrashHub.PlaceFreeBet,
/// MarginCallHub.OpenFreePosition).
/// </summary>
[Authorize]
public class PromoHub(PromoEngine promos) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var player = Player();
        await Groups.AddToGroupAsync(Context.ConnectionId, PromoEngine.GameGroup(player.GameId));
        await Groups.AddToGroupAsync(Context.ConnectionId, PromoEngine.PlayerGroup(player.PlayerId));
        await base.OnConnectedAsync();
    }

    public Task<object> GetState() => promos.StateForAsync(Player());

    private PlayerSession Player() =>
        SessionTokenService.FromPrincipal(Context.User!) ?? throw new HubException("Player identity missing from token");
}
