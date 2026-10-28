using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Application.LiveCrash;

/// <summary>One LiveCrashEngine per enabled game with Engine = LiveCrash, each with its own rounds, bets and RNG.</summary>
public sealed class LiveCrashRegistry
{
    private readonly Dictionary<string, LiveCrashEngine> _engines;

    public LiveCrashRegistry(IGameRegistry games, IGameRngService rng, IServiceScopeFactory scopes,
        IHubContext<LiveCrashHub> hub, ILoggerFactory loggers)
    {
        _engines = games.All
            .Where(g => g.Enabled && g.Engine == EngineKind.LiveCrash)
            .ToDictionary(
                g => g.GameId,
                g => new LiveCrashEngine(g, rng, scopes, hub, loggers.CreateLogger($"LiveCrash.{g.GameId}")),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<LiveCrashEngine> Engines => _engines.Values;

    public LiveCrashEngine? Find(string gameId) => _engines.GetValueOrDefault(gameId);
}
