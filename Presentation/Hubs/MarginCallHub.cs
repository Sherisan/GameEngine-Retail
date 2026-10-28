using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Application;
using Worldiator.Server.Application.MarginCall;

namespace Worldiator.Server.Presentation.Hubs;

/// <summary>
/// Margin Call (TradeX) client hub. Only sessions launched for the Margin Call game may connect.
/// Identity, currency and game come from the session token — never from method arguments.
/// Server → client events are documented on MarginCallEngine.
/// </summary>
[Authorize]
public class MarginCallHub(MarginCallEngine engine, WalletService wallet, ILogger<MarginCallHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var player = SessionTokenService.FromPrincipal(Context.User!);
        if (player == null || !string.Equals(player.GameId, engine.GameId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("MarginCallHub rejected a session for game {GameId}", player?.GameId);
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    /// <summary>Everything the client needs on (re)connect: config, markets with candle history, round, own positions, balance.</summary>
    public async Task<object> GetSnapshot()
    {
        var player = Player();
        var game   = engine.Game ?? throw new HubException("Game is not enabled.");
        var opt    = engine.Options;

        decimal? balance = null;
        try { balance = await wallet.GetBalanceAsync(player); }
        catch (Exception ex) { logger.LogWarning(ex, "MarginCall balance lookup failed for {PlayerId}", player.PlayerId); }

        return new
        {
            player = new { player.PlayerId, player.Username, player.Currency },
            balance,
            config = new
            {
                game.MinBet,
                game.MaxBet,
                game.MaxWin,
                opt.CommissionPercent,
                opt.SpreadBps,
                Multipliers = opt.AllowedMultipliers,
                opt.TickMs,
                opt.GrowthRate,
            },
            state = engine.Snapshot(player.PlayerId),
        };
    }

    public async Task<object> OpenPosition(string market, string direction, decimal stake, int multiplier)
    {
        var (position, balance, error) = await engine.OpenPositionAsync(Player(), market, direction, stake, multiplier);
        return new { ok = error == null, position, balance, error };
    }

    public async Task<decimal> GetBalance() => await wallet.GetBalanceAsync(Player());

    private PlayerSession Player() =>
        SessionTokenService.FromPrincipal(Context.User!) ?? throw new HubException("Player identity missing from token");
}
