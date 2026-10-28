using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Application;
using Worldiator.Server.Application.LiveCrash;

namespace Worldiator.Server.Presentation.Hubs;

/// <summary>
/// Client hub for every live crash game. The session token's game picks the engine, so one hub serves
/// any number of games and a connection only ever sees its own game's rounds (SignalR group per game).
/// Identity, currency and game come from the token — never from method arguments.
/// Server → client events are documented on LiveCrashEngine.
/// </summary>
[Authorize]
public class LiveCrashHub(LiveCrashRegistry registry, WalletService wallet, ILogger<LiveCrashHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var player = SessionTokenService.FromPrincipal(Context.User!);
        var engine = player == null ? null : registry.Find(player.GameId);
        if (engine == null)
        {
            logger.LogWarning("LiveCrashHub rejected a session for game {GameId}", player?.GameId);
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, engine.Group);
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveCrashEngine.PlayerGroup(engine.Game.GameId, player!.PlayerId));
        await base.OnConnectedAsync();
    }

    public async Task<object> GetSnapshot()
    {
        var (player, engine) = Resolve();
        var game = engine.Game;
        var live = engine.Settings;

        decimal? balance = null;
        try { balance = await wallet.GetBalanceAsync(player); }
        catch (Exception ex) { logger.LogWarning(ex, "Live crash balance lookup failed for {PlayerId}", player.PlayerId); }

        return new
        {
            player = new { player.PlayerId, player.Username, player.Currency },
            balance,
            config = new
            {
                game.GameId,
                game.Name,
                game.MinBet,
                game.MaxBet,
                game.MaxWin,
                live.GrowthRate,
                live.BettingMs,
                live.CrashedMs,
                live.TickMs,
                Slots = live.MaxBetsPerPlayer,
                live.MinAutoCashOut,
                MaxMultiplier = game.Rng!.MaxMultiplier,
            },
            state = engine.Snapshot(player.PlayerId),
        };
    }

    public async Task<object> PlaceBet(int slot, decimal amount, decimal? autoCashOut)
    {
        var (player, engine) = Resolve();
        var (bet, balance, error) = await engine.PlaceBetAsync(player, slot, amount, autoCashOut);
        return new { ok = error == null, bet, balance, error };
    }

    public async Task<object> CancelBet(int slot)
    {
        var (player, engine) = Resolve();
        var (balance, error) = await engine.CancelBetAsync(player, slot);
        return new { ok = error == null, balance, error };
    }

    public async Task<object> CashOut(int slot)
    {
        var (player, engine) = Resolve();
        var (multiplier, win, balance, error) = await engine.CashOutAsync(player, slot);
        return new { ok = error == null, multiplier, win, balance, error };
    }

    public async Task<decimal> GetBalance() => await wallet.GetBalanceAsync(Resolve().Player);

    private (PlayerSession Player, LiveCrashEngine Engine) Resolve()
    {
        var player = SessionTokenService.FromPrincipal(Context.User!) ?? throw new HubException("Player identity missing from token");
        var engine = registry.Find(player.GameId) ?? throw new HubException("This game is not a live crash game.");
        return (player, engine);
    }
}
