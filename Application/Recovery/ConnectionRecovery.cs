using System.Collections.Concurrent;

namespace Worldiator.Server.Application.Recovery;

/// <summary>
/// One bet that a lost connection left open, and what happened to it when the player came back.
///   Kind "refunded" — the bet was voided and Amount (the stake) went back to the player (a free bet goes back to them).
///   Kind "cashed"   — a step game that had already climbed was cashed out where it stood; Amount is the payout, which is
///                     always more than the stake, so dropping the connection never costs the player what they had won.
/// </summary>
public sealed record RecoveredBet(string GameId, string RoundId, string Kind, decimal Stake, decimal Amount, bool FreeBet = false, int? Slot = null);

/// <summary>
/// Connection-loss safety shared by every game (see SessionController, api/session/recover).
///
/// Crash games on a hub (GameHub, LiveCrashHub) refund a player's open bets the moment the server loses their last
/// connection to that game. The refunds are kept here as notices until the player reconnects and the game calls
/// api/session/recover, which also refunds anything still open (the client may notice a drop before the server does)
/// and settles an interrupted step-game round. The notices tell the player what was refunded.
///
/// Single-node, like the engines whose bets it refunds.
/// </summary>
public sealed class ConnectionRecovery
{
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, List<(RecoveredBet Bet, DateTime At)>> _notices = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _connections = new();

    private static string Key(string gameId, string playerId) => $"{gameId.ToLowerInvariant()}|{playerId}";

    // ── Notices ───────────────────────────────────────────────────────────

    public void Notify(string playerId, RecoveredBet bet)
    {
        var list = _notices.GetOrAdd(Key(bet.GameId, playerId), _ => []);
        lock (list)
        {
            list.RemoveAll(n => DateTime.UtcNow - n.At > NoticeLifetime);
            list.Add((bet, DateTime.UtcNow));
        }
    }

    /// <summary>The refunds made for this player in this game since they last asked, oldest first.</summary>
    public List<RecoveredBet> Drain(string gameId, string playerId)
    {
        if (!_notices.TryRemove(Key(gameId, playerId), out var list)) return [];
        lock (list) return list.Where(n => DateTime.UtcNow - n.At <= NoticeLifetime).Select(n => n.Bet).ToList();
    }

    // ── Hub connections ───────────────────────────────────────────────────

    /// <param name="scope">The hub's name plus the game id, so a player's promo-hub connection doesn't count as playing.</param>
    public void Connected(string scope, string playerId, string connectionId)
    {
        var set = _connections.GetOrAdd(Key(scope, playerId), _ => []);
        lock (set) set.Add(connectionId);
    }

    /// <summary>True when this was the player's last connection in the scope: nobody is left to cash their bets out.</summary>
    public bool Disconnected(string scope, string playerId, string connectionId)
    {
        var key = Key(scope, playerId);
        if (!_connections.TryGetValue(key, out var set)) return true;
        lock (set)
        {
            set.Remove(connectionId);
            if (set.Count > 0) return false;
            _connections.TryRemove(key, out _);
            return true;
        }
    }
}
