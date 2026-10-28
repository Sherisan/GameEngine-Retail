using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Presentation.DTOs;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Background;

/// <summary>
/// Runs the game loop as a background service and broadcasts events via SignalR.
///
/// SCALABILITY: When Redis is configured, uses leader election so exactly one node
/// runs the game loop. Followers contest every 5 seconds. The leader renews its lock
/// every 3 seconds (TTL = 10s) — if it dies, a follower takes over within ~10 seconds.
///
/// PERSISTENCE: After every round crash, saves to SQL asynchronously (fire-and-forget).
/// The game loop never blocks on DB writes.
/// </summary>
public class GameHostedService : BackgroundService
{
    private readonly GameEngine _engine;
    private readonly IHubContext<GameHub> _hub;
    private readonly IRoundRepository _roundCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _dbEnabled;
    private readonly ILeaderElection? _leader;
    private readonly ILogger<GameHostedService> _logger;

    public GameHostedService(
        GameEngine engine,
        IHubContext<GameHub> hub,
        IRoundRepository roundCache,
        IServiceScopeFactory scopeFactory,
        ILogger<GameHostedService> logger,
        ILeaderElection? leader = null)
    {
        _engine = engine;
        _hub = hub;
        _roundCache = roundCache;
        _scopeFactory = scopeFactory;
        using var probe = scopeFactory.CreateScope();
        _dbEnabled = probe.ServiceProvider.GetService<IAsyncRoundRepository>() != null;
        _leader = leader;
        _logger = logger;

        _engine.OnRoundStarted += OnRoundStarted;
        _engine.OnMultiplierUpdated += OnMultiplierUpdated;
        _engine.OnRoundCrashed += OnRoundCrashed;
        _engine.OnBetCashedOut += OnBetCashedOut;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Without Redis: behave exactly as before (single-node dev mode).
        if (_leader == null)
        {
            await _engine.StartRoundLoopAsync(ct);
            return;
        }

        // With Redis: compete for leadership. Only the leader runs the game loop.
        // Followers stay warm (serving connections + REST) and contest every 5 seconds.
        while (!ct.IsCancellationRequested)
        {
            if (!await _leader.TryBecomeLeaderAsync(ct))
            {
                _logger.LogDebug("Not the game loop leader — contesting in 5s");
                await Task.Delay(5_000, ct);
                continue;
            }

            // Won leadership. Run game loop + renew lock concurrently.
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var gameTask = _engine.StartRoundLoopAsync(loopCts.Token);
            var renewTask = RenewLeadershipLoopAsync(loopCts.Token);

            // Stop both if either exits (lock lost OR game loop crashed).
            await Task.WhenAny(gameTask, renewTask);
            await loopCts.CancelAsync();

            try { await Task.WhenAll(gameTask, renewTask); }
            catch (OperationCanceledException) { }

            await _leader.ReleaseLeadershipAsync();

            if (!ct.IsCancellationRequested)
            {
                // Brief pause before re-contesting — prevents instant tight loop on crash.
                _logger.LogWarning("Game loop exited — will re-contest leadership in 2s");
                await Task.Delay(2_000, ct);
            }
        }
    }

    private async Task RenewLeadershipLoopAsync(CancellationToken ct)
    {
        // Renew every 3s against a 10s TTL — gives 3 missed renewals before lock expires.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!await _leader!.RenewLeadershipAsync())
            {
                _logger.LogWarning("Failed to renew game loop leadership — another node took over");
                return;
            }
        }
    }

    private Task OnRoundStarted(Round round) =>
        _hub.Clients.All.SendAsync("RoundStarted", RoundStartedDto.From(round, _engine.WaitingSeconds));

    private Task OnMultiplierUpdated(decimal multiplier) =>
        _hub.Clients.All.SendAsync("MultiplierUpdated", multiplier);

    private Task OnRoundCrashed(Round round)
    {
        // Fire-and-forget DB write — game loop must never block on I/O.
        if (_dbEnabled)
            _ = PersistRoundAsync(round);

        var history = _roundCache.GetRecent(20).Select(HistoryEntryDto.From);
        return _hub.Clients.All.SendAsync("RoundCrashed", RoundCrashedDto.From(round, history));
    }

    private async Task PersistRoundAsync(Round round)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAsyncRoundRepository>();
            await repo.SaveRoundAsync(round);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist round {RoundId} to database", round.RoundId);
        }
    }

    private Task OnBetCashedOut(Bet bet) =>
        _hub.Clients.All.SendAsync("BetCashedOut", BetCashedOutDto.From(bet));
}
