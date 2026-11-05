using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Application.LiveCrash;

/// <summary>
/// One LiveCrashEngine per enabled game with Engine = LiveCrash, each with its own rounds, bets and RNG.
/// Built once at startup: a live crash game added, enabled or disabled in the back office starts / stops on the next restart.
/// </summary>
public sealed class LiveCrashRegistry
{
    private readonly Dictionary<string, LiveCrashEngine> _engines;

    public LiveCrashRegistry(IGameRegistry games, IGameRngService rng, IServiceScopeFactory scopes,
        IHubContext<LiveCrashHub> hub, PromoEngine promos, ILoggerFactory loggers)
    {
        _engines = games.All
            .Where(g => g.Enabled && g.Engine == EngineKind.LiveCrash)
            .ToDictionary(
                g => g.GameId,
                g => new LiveCrashEngine(g, games, rng, scopes, hub, promos, loggers.CreateLogger($"LiveCrash.{g.GameId}")),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<LiveCrashEngine> Engines => _engines.Values;

    public LiveCrashEngine? Find(string gameId) => _engines.GetValueOrDefault(gameId);
}
