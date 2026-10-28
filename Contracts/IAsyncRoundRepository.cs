using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>
/// Async persistence contract for completed rounds.
/// Used by GameHostedService (fire-and-forget writes) and admin endpoints (authoritative reads).
/// The in-process IRoundRepository stays fast for the game loop — this is the durable layer.
/// </summary>
public interface IAsyncRoundRepository
{
    /// <summary>Persist a completed round and all its bets. Idempotent (upsert semantics).</summary>
    Task SaveRoundAsync(Round round);

    /// <summary>Return the most recently completed rounds from the database, newest first.</summary>
    Task<IReadOnlyList<Round>> GetRecentAsync(int count = 50);

    /// <summary>Return a specific player's bet history, newest first.</summary>
    Task<IReadOnlyList<Bet>> GetBetsForPlayerAsync(string playerId, int count = 50);

    /// <summary>Return past round crash multipliers only (no bet data) — lighter than GetRecentAsync for a multiplier ticker.</summary>
    Task<IReadOnlyList<Round>> GetMultiplierHistoryAsync(int count = 50);
}
