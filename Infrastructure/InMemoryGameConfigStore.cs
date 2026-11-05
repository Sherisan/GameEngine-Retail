using System.Collections.Concurrent;
using System.Text.Json;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Game config held in memory, starting from appsettings "Games" and the built-in evolution ladders — used when
/// no SQL connection is configured. Back-office edits work but are lost on restart and aren't shared between instances.
/// </summary>
public class InMemoryGameConfigStore : IGameConfigStore
{
    private volatile IReadOnlyList<GameDefinition> _games;
    private readonly ConcurrentDictionary<string, IReadOnlyList<EvolutionTier>> _tiers =
        new(EvolutionTiers.Defaults, StringComparer.OrdinalIgnoreCase);
    private readonly List<GameConfigChange> _history = [];
    private long _nextAuditId = 1;

    public InMemoryGameConfigStore(IConfiguration config)
    {
        var games = GameCatalogue.FromConfiguration(config);
        GameCatalogue.Validate(games);
        _games = games;
    }

    public IReadOnlyList<GameDefinition> Games => _games;

    public IReadOnlyList<EvolutionTier> GetEvolutionTiers(string gameId) =>
        _tiers.TryGetValue(gameId, out var tiers) ? tiers : [];

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveGameAsync(GameDefinition game, string changedBy)
    {
        lock (_history)
        {
            var old = _games.FirstOrDefault(g => string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
            var games = _games.ToList();
            if (old == null) games.Add(game);
            else games[games.IndexOf(old)] = game;
            _games = games;
            Audit(game.GameId, GameConfigChange.GameSection, old == null ? null : GameCatalogue.AuditJson(old), GameCatalogue.AuditJson(game), changedBy);
        }
        return Task.CompletedTask;
    }

    public Task SaveEvolutionTiersAsync(string gameId, IReadOnlyList<EvolutionTier> tiers, string changedBy)
    {
        var ordered = tiers.OrderBy(t => t.Level).ToList();
        lock (_history)
        {
            var old = GetEvolutionTiers(gameId);
            _tiers[gameId] = ordered;
            Audit(gameId, GameConfigChange.EvolutionSection,
                old.Count == 0 ? null : JsonSerializer.Serialize(old), JsonSerializer.Serialize(ordered), changedBy);
        }
        return Task.CompletedTask;
    }

    // Must be called under lock (_history).
    private void Audit(string gameId, string section, string? oldValue, string newValue, string changedBy) =>
        _history.Add(new GameConfigChange(_nextAuditId++, gameId, section, oldValue, newValue, changedBy, DateTime.UtcNow));

    public Task<IReadOnlyList<GameConfigChange>> GetHistoryAsync(string gameId, string? section, int count)
    {
        lock (_history)
        {
            IReadOnlyList<GameConfigChange> result = _history
                .Where(c => string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase) && (section == null || c.Section == section))
                .OrderByDescending(c => c.AuditId)
                .Take(count)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
