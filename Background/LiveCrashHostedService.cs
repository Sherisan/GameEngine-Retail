using Worldiator.Server.Application.LiveCrash;

namespace Worldiator.Server.Background;

/// <summary>Runs every live crash game's round loop side by side (single node — see LiveCrashEngine).</summary>
public class LiveCrashHostedService(LiveCrashRegistry registry, ILogger<LiveCrashHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(registry.Engines.Select(async engine =>
        {
            try { await engine.RunAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogCritical(ex, "Live crash engine stopped: game={GameId}", engine.Game.GameId); }
        }));
}
