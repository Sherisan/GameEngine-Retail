using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// In-memory round storage — dev/testing only.
/// Swap for a real DB implementation of IRoundRepository without touching any other code.
/// </summary>
public class InMemoryRoundRepository : IRoundRepository
{
    private readonly List<Round> _rounds = new();
    private readonly object _lock = new();

    public void Add(Round round)
    {
        lock (_lock)
        {
            // Idempotent — safe to call from both the normal crash path and the error-recovery path.
            if (_rounds.Contains(round)) return;
            _rounds.Insert(0, round);
            if (_rounds.Count > 50) _rounds.RemoveAt(50);
        }
    }

    public IReadOnlyList<Round> GetRecent(int count = 50)
    {
        lock (_lock) { return _rounds.Take(count).ToList(); }
    }
}
