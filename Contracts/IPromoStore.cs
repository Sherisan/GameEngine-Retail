using Worldiator.Server.Domain.Promotions;

namespace Worldiator.Server.Contracts;

public enum AwardAddResult { Added, Exists, OverBudget }

/// <summary>
/// Promotions, the promo ledger (awards) and leaderboard scores. Promotions are served from an in-memory snapshot
/// (safe to read from game loops) refreshed by RefreshAsync; awards and scores are read and written directly.
/// SQL: Infrastructure/Persistence/Promotions.sql. Without a connection string, an in-memory store is used.
/// </summary>
public interface IPromoStore
{
    IReadOnlyList<Promotion> Promotions { get; }
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Inserts (PromotionId 0) or replaces a validated promotion and records the change. Returns it with its id.</summary>
    Task<Promotion> SaveAsync(Promotion promotion, string changedBy);
    Task<IReadOnlyList<PromotionChange>> GetHistoryAsync(int promotionId, int count);

    /// <summary>Cash paid or pending, per promotion (what counts against the budget).</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetSpentAsync();

    /// <summary>
    /// Adds an award unless one with the same AwardId exists. With enforceBudget, a cash award is only added while
    /// the promotion's paid + pending cash plus this amount stays within budget (checked atomically).
    /// Free bets are checked against the budget the same way (their stake), so a promotion can't give away more
    /// free-bet value than it has left.
    /// </summary>
    Task<AwardAddResult> AddAwardAsync(PromoAward award, decimal budget, bool enforceBudget);

    /// <summary>Saves status, settlement, game, currency, win, error and attempts of an existing award.</summary>
    Task UpdateAwardAsync(PromoAward award);

    Task<PromoAward?> GetAwardAsync(string awardId);

    /// <summary>
    /// Atomically moves one of the player's Available, unexpired free bets from these promotions to InPlay — the one
    /// expiring first — and returns it; null when they have none.
    /// </summary>
    Task<PromoAward?> ClaimFreeBetAsync(string playerId, IReadOnlyCollection<int> promotionIds, DateTime utcNow);

    /// <summary>Awards, newest first. Filters are optional.</summary>
    Task<IReadOnlyList<PromoAward>> ListAwardsAsync(int? promotionId, string? playerId, AwardStatus? status, int count);

    /// <summary>Number of awards a promotion has made (optionally only those created since).</summary>
    Task<int> CountAwardsAsync(int promotionId, DateTime? since = null);

    /// <summary>Distinct rounds a promotion has paid out on since a time (jackpot hits per day).</summary>
    Task<int> CountRoundsAsync(int promotionId, DateTime since);

    /// <summary>Marks Available free bets past ExpiresAt as Expired. Returns how many.</summary>
    Task<int> ExpireFreeBetsAsync(DateTime utcNow);

    /// <summary>Keeps the player's best score for the period (replaces it only when the new one is higher).</summary>
    Task UpsertScoreAsync(PromoScore score);
    /// <summary>Adds Score and Stake to the player's row for the period (creates it at zero first) — the "Total" metrics and cashback.</summary>
    Task AddScoreAsync(PromoScore score);
    Task<IReadOnlyList<PromoScore>> TopScoresAsync(int promotionId, string periodKey, int count);
    Task<PromoScore?> GetScoreAsync(int promotionId, string periodKey, string playerId);

    /// <summary>The player's progress in a promotion, or a new empty one.</summary>
    Task<PromoProgress> GetProgressAsync(int promotionId, string playerId);
    Task SaveProgressAsync(PromoProgress progress);

    /// <summary>Cash paid / pending / awaiting retry by a promotion since a time (hand jackpot pool left today).</summary>
    Task<decimal> SumCashAsync(int promotionId, DateTime since);
}
