using StackExchange.Redis;
using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure.Distributed;

/// <summary>
/// Redis-based distributed leader election for the game loop.
///
/// Problem: running N app instances without this means N simultaneous game loops,
/// each with their own round state — players on different servers see different games.
///
/// Solution: only the node that holds the Redis lock runs the game loop.
/// Other nodes remain warm (serving SignalR connections) and contest for leadership
/// every 5 seconds. If the leader dies, the lock expires after 10 seconds and a
/// follower takes over automatically.
///
/// The Lua scripts below are atomic — no race between check and update.
/// </summary>
public class RedisLeaderElection : ILeaderElection
{
    private readonly IDatabase _db;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ILogger<RedisLeaderElection> _logger;

    private const string LockKey = "worldiator:gameloop:leader";
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(10);

    // Extend only if we still own the lock — prevents a stale node from extending after losing it.
    private const string RenewScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return 0
        end";

    // Delete only if we still own the lock — prevents deleting another node's lock on restart.
    private const string ReleaseScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end";

    public RedisLeaderElection(IConnectionMultiplexer redis, ILogger<RedisLeaderElection> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<bool> TryBecomeLeaderAsync(CancellationToken ct)
    {
        var acquired = await _db.StringSetAsync(
            LockKey, _instanceId, LockTtl, When.NotExists);

        if (acquired)
            _logger.LogInformation("Node {InstanceId} acquired game loop leadership", _instanceId[..8]);

        return acquired;
    }

    public async Task<bool> RenewLeadershipAsync()
    {
        var result = await _db.ScriptEvaluateAsync(
            RenewScript,
            new RedisKey[] { LockKey },
            new RedisValue[] { _instanceId, (long)LockTtl.TotalMilliseconds });

        return (long)result == 1;
    }

    public async Task ReleaseLeadershipAsync()
    {
        await _db.ScriptEvaluateAsync(
            ReleaseScript,
            new RedisKey[] { LockKey },
            new RedisValue[] { _instanceId });

        _logger.LogInformation("Node {InstanceId} released game loop leadership", _instanceId[..8]);
    }
}
