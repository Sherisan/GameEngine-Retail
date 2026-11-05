using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>
/// Game configuration edited from the Game Back Office: the game catalogue (limits, RNG tables, timings…)
/// and the evolution ladders. Reads come from an in-memory snapshot (safe to call from game loops under their
/// locks); the snapshot is refreshed from storage by GameConfigRefreshService so every API instance picks up a
/// change within a few seconds. Definitions handed out are shared — never modify one, clone it
/// (GameCatalogue.Clone) and save the copy.
/// </summary>
public interface IGameConfigStore
{
    /// <summary>Every game, enabled or not, in catalogue order.</summary>
    IReadOnlyList<GameDefinition> Games { get; }

    /// <summary>The evolution ladder for a game, ordered by level. Empty when the game has none. Never blocks.</summary>
    IReadOnlyList<EvolutionTier> GetEvolutionTiers(string gameId);

    /// <summary>
    /// Startup: adds the games from appsettings that storage doesn't have yet (never overwrites), then loads
    /// the snapshot. Called once before the game engines start.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Reloads the snapshot from storage.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces one game (already validated against the whole catalogue) and records the change.</summary>
    Task SaveGameAsync(GameDefinition game, string changedBy);

    /// <summary>Replaces a game's ladder (already validated) and records the change in the audit trail.</summary>
    Task SaveEvolutionTiersAsync(string gameId, IReadOnlyList<EvolutionTier> tiers, string changedBy);

    /// <summary>Most recent changes first. Section null = every section.</summary>
    Task<IReadOnlyList<GameConfigChange>> GetHistoryAsync(string gameId, string? section, int count);
}
