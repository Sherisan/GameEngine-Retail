using Worldiator.Server.Contracts;

namespace Worldiator.Server.Background;

/// <summary>
/// Reloads back-office game config from storage every BackOffice:ConfigRefreshSeconds (the first load happens in
/// Program.cs before the engines start), so a change saved on one API instance reaches the others — and the game
/// loops — without a restart. A failed read keeps the last good snapshot.
/// </summary>
public class GameConfigRefreshService(IGameConfigStore store, IConfiguration config, ILogger<GameConfigRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, config.GetValue("BackOffice:ConfigRefreshSeconds", 15)));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await store.RefreshAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "Game config refresh failed — serving the last loaded config"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
