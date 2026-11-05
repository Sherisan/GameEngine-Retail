using System.Text.Json;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Promotions;

namespace Worldiator.Server.Infrastructure;

/// <summary>IPromoStore without a database (dev / tests). Everything is lost on restart.</summary>
public class InMemoryPromoStore : IPromoStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Promotion> _promotions = [];
    private readonly Dictionary<string, PromoAward> _awards = [];
    private readonly Dictionary<(int, string, string), PromoScore> _scores = [];
    private readonly Dictionary<(int, string), PromoProgress> _progress = [];
    private readonly List<PromotionChange> _history = [];
    private volatile IReadOnlyList<Promotion> _snapshot = [];
    private int _nextId = 1;
    private long _nextAuditId = 1;

    public IReadOnlyList<Promotion> Promotions => _snapshot;

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<Promotion> SaveAsync(Promotion promotion, string changedBy)
    {
        lock (_lock)
        {
            var copy = Clone(promotion);
            string? old = null;
            if (copy.PromotionId == 0) copy.PromotionId = _nextId++;
            else if (_promotions.TryGetValue(copy.PromotionId, out var existing)) old = Json(existing);
            _promotions[copy.PromotionId] = copy;
            _history.Add(new PromotionChange(_nextAuditId++, copy.PromotionId, old, Json(copy), changedBy, DateTime.UtcNow));
            _snapshot = _promotions.Values.OrderBy(p => p.PromotionId).ToList();
            return Task.FromResult(copy);
        }
    }

    public Task<IReadOnlyList<PromotionChange>> GetHistoryAsync(int promotionId, int count)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PromotionChange>>(
                _history.Where(h => h.PromotionId == promotionId).OrderByDescending(h => h.AuditId).Take(count).ToList());
    }

    public Task<IReadOnlyDictionary<int, decimal>> GetSpentAsync()
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyDictionary<int, decimal>>(
                _awards.Values.GroupBy(a => a.PromotionId).ToDictionary(g => g.Key, g => g.Sum(Cost)));
    }

    public Task<AwardAddResult> AddAwardAsync(PromoAward award, decimal budget, bool enforceBudget)
    {
        lock (_lock)
        {
            if (_awards.ContainsKey(award.AwardId)) return Task.FromResult(AwardAddResult.Exists);
            if (enforceBudget)
            {
                var spent = _awards.Values.Where(a => a.PromotionId == award.PromotionId).Sum(Cost);
                if (spent + award.Amount > budget) return Task.FromResult(AwardAddResult.OverBudget);
            }
            _awards[award.AwardId] = CloneAward(award);
            return Task.FromResult(AwardAddResult.Added);
        }
    }

    public Task UpdateAwardAsync(PromoAward award)
    {
        lock (_lock) _awards[award.AwardId] = CloneAward(award);
        return Task.CompletedTask;
    }

    public Task<PromoAward?> GetAwardAsync(string awardId)
    {
        lock (_lock) return Task.FromResult(_awards.TryGetValue(awardId, out var a) ? CloneAward(a) : null);
    }

    public Task<PromoAward?> ClaimFreeBetAsync(string playerId, IReadOnlyCollection<int> promotionIds, DateTime utcNow)
    {
        lock (_lock)
        {
            var award = _awards.Values
                .Where(a => a.Kind == AwardKind.FreeBet && a.Status == AwardStatus.Available && a.PlayerId == playerId
                         && promotionIds.Contains(a.PromotionId) && (a.ExpiresAt == null || a.ExpiresAt > utcNow))
                .OrderBy(a => a.ExpiresAt ?? DateTime.MaxValue).ThenBy(a => a.CreatedAt)
                .FirstOrDefault();
            if (award == null) return Task.FromResult<PromoAward?>(null);
            award.Status = AwardStatus.InPlay;
            return Task.FromResult<PromoAward?>(CloneAward(award));
        }
    }

    public Task<IReadOnlyList<PromoAward>> ListAwardsAsync(int? promotionId, string? playerId, AwardStatus? status, int count)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PromoAward>>(_awards.Values
                .Where(a => (promotionId == null || a.PromotionId == promotionId)
                         && (playerId == null || a.PlayerId == playerId)
                         && (status == null || a.Status == status))
                .OrderByDescending(a => a.CreatedAt).Take(count).Select(CloneAward).ToList());
    }

    public Task<int> CountAwardsAsync(int promotionId, DateTime? since = null)
    {
        lock (_lock)
            return Task.FromResult(_awards.Values.Count(a => a.PromotionId == promotionId && a.ParentAwardId == null
                                                          && (since == null || a.CreatedAt >= since)));
    }

    public Task<int> CountRoundsAsync(int promotionId, DateTime since)
    {
        lock (_lock)
            return Task.FromResult(_awards.Values.Where(a => a.PromotionId == promotionId && a.RoundId != null && a.CreatedAt >= since)
                .Select(a => a.RoundId).Distinct().Count());
    }

    public Task<int> ExpireFreeBetsAsync(DateTime utcNow)
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var a in _awards.Values.Where(a => a.Kind == AwardKind.FreeBet && a.Status == AwardStatus.Available && a.ExpiresAt <= utcNow))
            {
                a.Status = AwardStatus.Expired;
                a.SettledAt = utcNow;
                n++;
            }
            return Task.FromResult(n);
        }
    }

    public Task UpsertScoreAsync(PromoScore score)
    {
        lock (_lock)
        {
            var key = (score.PromotionId, score.PeriodKey, score.PlayerId);
            if (!_scores.TryGetValue(key, out var best) || score.Score > best.Score)
                _scores[key] = score;
        }
        return Task.CompletedTask;
    }

    public Task AddScoreAsync(PromoScore score)
    {
        lock (_lock)
        {
            var key = (score.PromotionId, score.PeriodKey, score.PlayerId);
            if (_scores.TryGetValue(key, out var row))
            {
                row.Score += score.Score;
                row.Stake += score.Stake;
                row.Username = score.Username;
                row.Currency = score.Currency;
                row.GameId = score.GameId;
                row.AchievedAt = score.AchievedAt;
            }
            else _scores[key] = score;
        }
        return Task.CompletedTask;
    }

    public Task<PromoScore?> GetScoreAsync(int promotionId, string periodKey, string playerId)
    {
        lock (_lock) return Task.FromResult(_scores.GetValueOrDefault((promotionId, periodKey, playerId)));
    }

    public Task<PromoProgress> GetProgressAsync(int promotionId, string playerId)
    {
        lock (_lock)
        {
            var p = _progress.GetValueOrDefault((promotionId, playerId));
            return Task.FromResult(p == null ? new PromoProgress { PromotionId = promotionId, PlayerId = playerId } : new PromoProgress
            {
                PromotionId = p.PromotionId, PlayerId = p.PlayerId, Count = p.Count, Streak = p.Streak, Completed = p.Completed,
                Data = p.Data, LastAt = p.LastAt, DayKey = p.DayKey, DayCount = p.DayCount,
            });
        }
    }

    public Task SaveProgressAsync(PromoProgress progress)
    {
        lock (_lock) _progress[(progress.PromotionId, progress.PlayerId)] = progress;
        return Task.CompletedTask;
    }

    public Task<decimal> SumCashAsync(int promotionId, DateTime since)
    {
        lock (_lock)
            return Task.FromResult(_awards.Values
                .Where(a => a.PromotionId == promotionId && a.Kind == AwardKind.Cash && a.CreatedAt >= since
                         && a.Status is AwardStatus.Pending or AwardStatus.Paid or AwardStatus.Failed)
                .Sum(a => a.Amount));
    }

    public Task<IReadOnlyList<PromoScore>> TopScoresAsync(int promotionId, string periodKey, int count)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PromoScore>>(_scores.Values
                .Where(s => s.PromotionId == promotionId && s.PeriodKey == periodKey)
                .OrderByDescending(s => s.Score).ThenBy(s => s.AchievedAt).Take(count).ToList());
    }

    /// <summary>
    /// What an award costs the budget (same rule as SqlPromoStore): cash paid, pending or awaiting retry; a free bet's
    /// stake while it can still be played; once played, only the winnings it paid (its own cash award).
    /// </summary>
    private static decimal Cost(PromoAward a) => a.Kind switch
    {
        AwardKind.Cash => a.Status is AwardStatus.Pending or AwardStatus.Paid or AwardStatus.Failed ? a.Amount : 0,
        _ => a.Status is AwardStatus.Available or AwardStatus.InPlay ? a.Amount : 0,
    };

    private static Promotion Clone(Promotion p) => JsonSerializer.Deserialize<Promotion>(JsonSerializer.Serialize(p, GameCatalogue.Json), GameCatalogue.Json)!;
    private static string Json(Promotion p) => JsonSerializer.Serialize(p, GameCatalogue.Json);

    private static PromoAward CloneAward(PromoAward a) => new()
    {
        AwardId = a.AwardId, PromotionId = a.PromotionId, GameId = a.GameId, PlayerId = a.PlayerId, Username = a.Username,
        Currency = a.Currency, Kind = a.Kind, Amount = a.Amount, Reason = a.Reason, Status = a.Status, CreatedAt = a.CreatedAt,
        SettledAt = a.SettledAt, ExpiresAt = a.ExpiresAt, RoundId = a.RoundId, WinAmount = a.WinAmount,
        ParentAwardId = a.ParentAwardId, Error = a.Error, Attempts = a.Attempts,
    };
}
