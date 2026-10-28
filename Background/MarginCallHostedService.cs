using Worldiator.Server.Application.MarginCall;

namespace Worldiator.Server.Background;

/// <summary>Runs the Margin Call round loop for the lifetime of the app (single node — see MarginCallEngine).</summary>
public class MarginCallHostedService(MarginCallEngine engine, ILogger<MarginCallHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await engine.RunAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogCritical(ex, "MarginCall engine stopped"); }
    }
}
