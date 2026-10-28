using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>
/// Persistence contract for completed rounds.
/// Currently backed by InMemoryRoundRepository — swap for a real DB implementation
/// without touching any other code when the DB layer arrives.
/// </summary>
public interface IRoundRepository
{
    /// <summary>Persist a completed round. Idempotent — safe to call twice for the same round.</summary>
    void Add(Round round);

    /// <summary>Return the most recently completed rounds, newest first.</summary>
    IReadOnlyList<Round> GetRecent(int count = 50);
}
