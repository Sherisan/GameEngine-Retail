namespace Worldiator.Server.Contracts;

/// <summary>
/// Distributed leader election contract.
/// Ensures exactly one node runs the game loop at any time.
/// Without this, N nodes = N independent game loops = N parallel rounds (catastrophic).
/// </summary>
public interface ILeaderElection
{
    /// <summary>Try to acquire leadership. Returns true if this node is now the leader.</summary>
    Task<bool> TryBecomeLeaderAsync(CancellationToken ct);

    /// <summary>
    /// Renew the leadership lock before it expires.
    /// Returns false if another node has taken over (lock was lost).
    /// </summary>
    Task<bool> RenewLeadershipAsync();

    /// <summary>Voluntarily release leadership — called on graceful shutdown.</summary>
    Task ReleaseLeadershipAsync();
}
