using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Worldiator.Server.Domain.Promotions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AwardKind { Cash, FreeBet }

/// <summary>
/// Cash:    Pending → Paid, or Failed (retried with the same wallet reference until Paid).
/// FreeBet: Available → InPlay (a bet is using it) → Used, or back to Available if the bet didn't go through;
///          Expired after ExpiresAt; Cancelled from the back office.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AwardStatus { Pending, Paid, Failed, Available, InPlay, Used, Expired, Cancelled }

/// <summary>
/// One thing a promotion gave one player — the promo ledger. AwardId is deterministic for anything that must
/// happen once (a race win, a leaderboard rank, a rain share), so a retry or a second instance can't pay twice.
/// Cash awards are credited to the wallet as "PROMO_&lt;AwardId&gt;" under the round "&lt;game&gt;:promo-&lt;PromotionId&gt;".
/// </summary>
public class PromoAward
{
    public string AwardId { get; set; } = NewId();
    public int PromotionId { get; set; }
    /// <summary>Where it was won / used. Empty for a free bet not used yet (it's valid in any of the promotion's games).</summary>
    public string GameId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    /// <summary>Empty for a free bet granted from the back office until it's used.</summary>
    public string Currency { get; set; } = string.Empty;
    public AwardKind Kind { get; set; }
    /// <summary>Cash paid, or the free bet's stake.</summary>
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public AwardStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Round the award came from, or the round a free bet was played in.</summary>
    public string? RoundId { get; set; }
    /// <summary>Free bet: what it returned (before the stake is taken off).</summary>
    public decimal? WinAmount { get; set; }
    /// <summary>A free bet's winnings payout points back at the free bet.</summary>
    public string? ParentAwardId { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Same parts → same id (32 hex chars), for awards that must only ever happen once.</summary>
    public static string IdFor(params object[] parts)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash).ToString("N");
    }
}

/// <summary>A player's best qualifying result in one leaderboard period.</summary>
public class PromoScore
{
    public int PromotionId { get; set; }
    /// <summary>"all", "2026-11-03" (daily) or "2026-W45" (weekly), UTC.</summary>
    public string PeriodKey { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    /// <summary>The leaderboard metric (best multiple, best win, total wagered, …), or cashback: the weighted net loss.</summary>
    public decimal Score { get; set; }
    public decimal Stake { get; set; }
    public DateTime AchievedAt { get; set; }
}

/// <summary>A change to a promotion, for its history in the back office.</summary>
public record PromotionChange(long AuditId, int PromotionId, string? OldValue, string NewValue, string ChangedBy, DateTime ChangedAt);

/// <summary>
/// A player's running state in one promotion: buy-X card count, streak, collected symbols, per-day caps.
/// One row per (promotion, player); PromoEngine reads and writes it while handling that player's play.
/// </summary>
public class PromoProgress
{
    public int PromotionId { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    /// <summary>Buy-X: paid cards towards the next free one.</summary>
    public int Count { get; set; }
    /// <summary>Streak missions: plays / wins in a row.</summary>
    public int Streak { get; set; }
    /// <summary>Rewards earned so far (sets completed, streaks finished, free cards given).</summary>
    public int Completed { get; set; }
    /// <summary>Collection: the symbols collected, comma separated.</summary>
    public string Data { get; set; } = string.Empty;
    public DateTime? LastAt { get; set; }
    /// <summary>UTC date of DayCount, for per-day caps.</summary>
    public string DayKey { get; set; } = string.Empty;
    public int DayCount { get; set; }

    public int TodayCount(DateTime utc) => DayKey == utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ? DayCount : 0;

    public void CountToday(DateTime utc)
    {
        var key = utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (DayKey != key) { DayKey = key; DayCount = 0; }
        DayCount++;
    }
}
