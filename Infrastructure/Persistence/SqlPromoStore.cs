using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Promotions;

namespace Worldiator.Server.Infrastructure.Persistence;

/// <summary>
/// SQL Server promotions and promo ledger (Promotions.sql). Promotions are served from a snapshot swapped in by
/// RefreshAsync; until the tables exist the snapshot is empty, so no promotion runs.
/// </summary>
public class SqlPromoStore(IConfiguration config, ILogger<SqlPromoStore> logger) : IPromoStore
{
    private const int InvalidObjectName = 208;
    private const string AwardColumns =
        "AwardId, PromotionId, GameId, PlayerId, Username, Currency, Kind, Amount, Reason, Status, CreatedAt, SettledAt, " +
        "ExpiresAt, RoundId, WinAmount, ParentAwardId, Error, Attempts";

    // Same rule as InMemoryPromoStore.Cost: cash paid / pending / awaiting retry, plus free bets that can still be played.
    private const string CostSql =
        "ISNULL(SUM(CASE WHEN Kind = 'Cash' AND Status IN ('Pending','Paid','Failed') THEN Amount " +
        "WHEN Kind = 'FreeBet' AND Status IN ('Available','InPlay') THEN Amount ELSE 0 END), 0)";

    private readonly string _connStr = config.GetConnectionString("DefaultConnection")!;
    private volatile IReadOnlyList<Promotion> _promotions = [];
    private bool _missingLogged;

    public IReadOnlyList<Promotion> Promotions => _promotions;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var conn = new SqlConnection(_connStr);
            var rows = await conn.QueryAsync<(int PromotionId, string ConfigJson)>(new CommandDefinition(
                "SELECT PromotionId, ConfigJson FROM dbo.Promotions ORDER BY PromotionId", cancellationToken: ct));
            var list = new List<Promotion>();
            foreach (var (id, json) in rows)
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Promotion>(json, GameCatalogue.Json)!;
                    p.PromotionId = id;
                    p.Validate();
                    list.Add(p);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    var previous = _promotions.FirstOrDefault(p => p.PromotionId == id);
                    logger.LogError("dbo.Promotions row {PromotionId} is invalid ({Error}) — {Action}", id, ex.Message,
                        previous == null ? "skipping it" : "keeping the previous version");
                    if (previous != null) list.Add(previous);
                }
            }
            _promotions = list;
        }
        catch (SqlException ex) when (ex.Number == InvalidObjectName)
        {
            if (!_missingLogged)
                logger.LogWarning("dbo.Promotions doesn't exist — no promotions will run. Run Infrastructure/Persistence/Promotions.sql.");
            _missingLogged = true;
        }
    }

    public async Task<Promotion> SaveAsync(Promotion promotion, string changedBy)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        var p = JsonSerializer.Deserialize<Promotion>(JsonSerializer.Serialize(promotion, GameCatalogue.Json), GameCatalogue.Json)!;
        string? old = null;

        if (p.PromotionId == 0)
        {
            p.PromotionId = await conn.QuerySingleAsync<int>(
                "INSERT INTO dbo.Promotions (Name, Type, Enabled, StartsAt, EndsAt, Budget, ConfigJson, UpdatedBy) OUTPUT INSERTED.PromotionId " +
                "VALUES (@Name, @Type, @Enabled, @StartsAt, @EndsAt, @Budget, N'{}', @ChangedBy)",
                new { p.Name, Type = p.Type.ToString(), p.Enabled, p.StartsAt, p.EndsAt, p.Budget, ChangedBy = changedBy }, tx);
        }
        else
        {
            old = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT ConfigJson FROM dbo.Promotions WITH (UPDLOCK) WHERE PromotionId = @PromotionId", new { p.PromotionId }, tx)
                ?? throw new KeyNotFoundException($"Promotion {p.PromotionId} doesn't exist.");
        }

        var json = JsonSerializer.Serialize(p, GameCatalogue.Json);
        await conn.ExecuteAsync(
            "UPDATE dbo.Promotions SET Name = @Name, Type = @Type, Enabled = @Enabled, StartsAt = @StartsAt, EndsAt = @EndsAt, " +
            "Budget = @Budget, ConfigJson = @Json, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy WHERE PromotionId = @PromotionId",
            new { p.PromotionId, p.Name, Type = p.Type.ToString(), p.Enabled, p.StartsAt, p.EndsAt, p.Budget, Json = json, ChangedBy = changedBy }, tx);
        await conn.ExecuteAsync(
            "INSERT INTO dbo.PromotionAudit (PromotionId, OldValue, NewValue, ChangedBy) VALUES (@PromotionId, @old, @json, @changedBy)",
            new { p.PromotionId, old, json, changedBy }, tx);
        tx.Commit();

        // This instance serves it straight away; others on their next refresh.
        var list = _promotions.Where(x => x.PromotionId != p.PromotionId).Append(p).OrderBy(x => x.PromotionId).ToList();
        _promotions = list;
        return p;
    }

    public async Task<IReadOnlyList<PromotionChange>> GetHistoryAsync(int promotionId, int count)
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<PromotionChange>(
            "SELECT TOP (@count) AuditId, PromotionId, OldValue, NewValue, ChangedBy, ChangedAt FROM dbo.PromotionAudit " +
            "WHERE PromotionId = @promotionId ORDER BY ChangedAt DESC, AuditId DESC", new { promotionId, count });
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetSpentAsync()
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<(int PromotionId, decimal Spent)>(
            $"SELECT PromotionId, {CostSql} AS Spent FROM dbo.PromoAwards GROUP BY PromotionId");
        return rows.ToDictionary(r => r.PromotionId, r => r.Spent);
    }

    public async Task<AwardAddResult> AddAwardAsync(PromoAward award, decimal budget, bool enforceBudget)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        // One writer per promotion across every API instance, so two awards can't both squeeze under the budget.
        await conn.ExecuteAsync("EXEC sp_getapplock @Resource, 'Exclusive', 'Transaction', 10000",
            new { Resource = $"promo:{award.PromotionId}" }, tx);

        if (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.PromoAwards WHERE AwardId = @AwardId", new { award.AwardId }, tx) > 0)
            return AwardAddResult.Exists;
        if (enforceBudget)
        {
            var spent = await conn.ExecuteScalarAsync<decimal>(
                $"SELECT {CostSql} FROM dbo.PromoAwards WHERE PromotionId = @PromotionId", new { award.PromotionId }, tx);
            if (spent + award.Amount > budget) return AwardAddResult.OverBudget;
        }

        await conn.ExecuteAsync(
            $"INSERT INTO dbo.PromoAwards ({AwardColumns}) VALUES (@AwardId, @PromotionId, @GameId, @PlayerId, @Username, @Currency, @Kind, " +
            "@Amount, @Reason, @Status, @CreatedAt, @SettledAt, @ExpiresAt, @RoundId, @WinAmount, @ParentAwardId, @Error, @Attempts)",
            Params(award), tx);
        tx.Commit();
        return AwardAddResult.Added;
    }

    public async Task UpdateAwardAsync(PromoAward award)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync(
            "UPDATE dbo.PromoAwards SET GameId = @GameId, Username = @Username, Currency = @Currency, Status = @Status, SettledAt = @SettledAt, " +
            "RoundId = @RoundId, WinAmount = @WinAmount, Error = @Error, Attempts = @Attempts WHERE AwardId = @AwardId",
            Params(award));
    }

    public async Task<PromoAward?> GetAwardAsync(string awardId)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.QuerySingleOrDefaultAsync<PromoAward>($"SELECT {AwardColumns} FROM dbo.PromoAwards WHERE AwardId = @awardId", new { awardId });
    }

    public async Task<PromoAward?> ClaimFreeBetAsync(string playerId, IReadOnlyCollection<int> promotionIds, DateTime utcNow)
    {
        if (promotionIds.Count == 0) return null;
        using var conn = new SqlConnection(_connStr);
        return await conn.QuerySingleOrDefaultAsync<PromoAward>(
            $"""
            WITH c AS (
                SELECT TOP (1) * FROM dbo.PromoAwards WITH (UPDLOCK, ROWLOCK, READPAST)
                WHERE PlayerId = @playerId AND Kind = 'FreeBet' AND Status = 'Available' AND PromotionId IN @promotionIds
                  AND (ExpiresAt IS NULL OR ExpiresAt > @utcNow)
                ORDER BY ISNULL(ExpiresAt, '9999-12-31'), CreatedAt)
            UPDATE c SET Status = 'InPlay'
            OUTPUT {string.Join(", ", AwardColumns.Split(", ").Select(c => "INSERTED." + c))};
            """,
            new { playerId, promotionIds, utcNow });
    }

    public async Task<IReadOnlyList<PromoAward>> ListAwardsAsync(int? promotionId, string? playerId, AwardStatus? status, int count)
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<PromoAward>(
            $"SELECT TOP (@count) {AwardColumns} FROM dbo.PromoAwards WHERE (@promotionId IS NULL OR PromotionId = @promotionId) " +
            "AND (@playerId IS NULL OR PlayerId = @playerId) AND (@status IS NULL OR Status = @status) ORDER BY CreatedAt DESC",
            new { count, promotionId, playerId, status = status?.ToString() });
        return rows.ToList();
    }

    public async Task<int> CountAwardsAsync(int promotionId, DateTime? since = null)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PromoAwards WHERE PromotionId = @promotionId AND ParentAwardId IS NULL AND (@since IS NULL OR CreatedAt >= @since)",
            new { promotionId, since });
    }

    public async Task<int> CountRoundsAsync(int promotionId, DateTime since)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT RoundId) FROM dbo.PromoAwards WHERE PromotionId = @promotionId AND RoundId IS NOT NULL AND CreatedAt >= @since",
            new { promotionId, since });
    }

    public async Task<int> ExpireFreeBetsAsync(DateTime utcNow)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.ExecuteAsync(
            "UPDATE dbo.PromoAwards SET Status = 'Expired', SettledAt = @utcNow WHERE Kind = 'FreeBet' AND Status = 'Available' AND ExpiresAt <= @utcNow",
            new { utcNow });
    }

    public async Task UpsertScoreAsync(PromoScore s)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync(
            """
            MERGE dbo.PromoScores WITH (HOLDLOCK) AS t
            USING (SELECT @PromotionId AS PromotionId, @PeriodKey AS PeriodKey, @PlayerId AS PlayerId) AS s
               ON t.PromotionId = s.PromotionId AND t.PeriodKey = s.PeriodKey AND t.PlayerId = s.PlayerId
            WHEN MATCHED AND t.Score < @Score THEN UPDATE SET
                Username = @Username, Currency = @Currency, GameId = @GameId, Score = @Score, Stake = @Stake, AchievedAt = @AchievedAt
            WHEN NOT MATCHED THEN INSERT (PromotionId, PeriodKey, PlayerId, Username, Currency, GameId, Score, Stake, AchievedAt)
                VALUES (@PromotionId, @PeriodKey, @PlayerId, @Username, @Currency, @GameId, @Score, @Stake, @AchievedAt);
            """, s);
    }

    public async Task AddScoreAsync(PromoScore s)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync(
            """
            MERGE dbo.PromoScores WITH (HOLDLOCK) AS t
            USING (SELECT @PromotionId AS PromotionId, @PeriodKey AS PeriodKey, @PlayerId AS PlayerId) AS s
               ON t.PromotionId = s.PromotionId AND t.PeriodKey = s.PeriodKey AND t.PlayerId = s.PlayerId
            WHEN MATCHED THEN UPDATE SET
                Username = @Username, Currency = @Currency, GameId = @GameId, Score = t.Score + @Score, Stake = t.Stake + @Stake, AchievedAt = @AchievedAt
            WHEN NOT MATCHED THEN INSERT (PromotionId, PeriodKey, PlayerId, Username, Currency, GameId, Score, Stake, AchievedAt)
                VALUES (@PromotionId, @PeriodKey, @PlayerId, @Username, @Currency, @GameId, @Score, @Stake, @AchievedAt);
            """, s);
    }

    public async Task<PromoScore?> GetScoreAsync(int promotionId, string periodKey, string playerId)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.QuerySingleOrDefaultAsync<PromoScore>(
            "SELECT PromotionId, PeriodKey, PlayerId, Username, Currency, GameId, Score, Stake, AchievedAt FROM dbo.PromoScores " +
            "WHERE PromotionId = @promotionId AND PeriodKey = @periodKey AND PlayerId = @playerId",
            new { promotionId, periodKey, playerId });
    }

    public async Task<PromoProgress> GetProgressAsync(int promotionId, string playerId)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.QuerySingleOrDefaultAsync<PromoProgress>(
                   "SELECT PromotionId, PlayerId, [Count], Streak, Completed, Data, LastAt, DayKey, DayCount FROM dbo.PromoProgress " +
                   "WHERE PromotionId = @promotionId AND PlayerId = @playerId", new { promotionId, playerId })
               ?? new PromoProgress { PromotionId = promotionId, PlayerId = playerId };
    }

    public async Task SaveProgressAsync(PromoProgress p)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync(
            """
            MERGE dbo.PromoProgress WITH (HOLDLOCK) AS t
            USING (SELECT @PromotionId AS PromotionId, @PlayerId AS PlayerId) AS s
               ON t.PromotionId = s.PromotionId AND t.PlayerId = s.PlayerId
            WHEN MATCHED THEN UPDATE SET [Count] = @Count, Streak = @Streak, Completed = @Completed, Data = @Data, LastAt = @LastAt,
                DayKey = @DayKey, DayCount = @DayCount
            WHEN NOT MATCHED THEN INSERT (PromotionId, PlayerId, [Count], Streak, Completed, Data, LastAt, DayKey, DayCount)
                VALUES (@PromotionId, @PlayerId, @Count, @Streak, @Completed, @Data, @LastAt, @DayKey, @DayCount);
            """, p);
    }

    public async Task<decimal> SumCashAsync(int promotionId, DateTime since)
    {
        using var conn = new SqlConnection(_connStr);
        return await conn.ExecuteScalarAsync<decimal>(
            "SELECT ISNULL(SUM(Amount), 0) FROM dbo.PromoAwards WHERE PromotionId = @promotionId AND Kind = 'Cash' " +
            "AND Status IN ('Pending','Paid','Failed') AND CreatedAt >= @since", new { promotionId, since });
    }

    public async Task<IReadOnlyList<PromoScore>> TopScoresAsync(int promotionId, string periodKey, int count)
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<PromoScore>(
            "SELECT TOP (@count) PromotionId, PeriodKey, PlayerId, Username, Currency, GameId, Score, Stake, AchievedAt FROM dbo.PromoScores " +
            "WHERE PromotionId = @promotionId AND PeriodKey = @periodKey ORDER BY Score DESC, AchievedAt",
            new { promotionId, periodKey, count });
        return rows.ToList();
    }

    // Enums go to SQL as their names (Dapper would send numbers), matching the Kind / Status columns.
    private static object Params(PromoAward a) => new
    {
        a.AwardId, a.PromotionId, a.GameId, a.PlayerId, a.Username, a.Currency, Kind = a.Kind.ToString(), a.Amount, a.Reason,
        Status = a.Status.ToString(), a.CreatedAt, a.SettledAt, a.ExpiresAt, a.RoundId, a.WinAmount, a.ParentAwardId,
        Error = a.Error is { Length: > 500 } e ? e[..500] : a.Error, a.Attempts,
    };
}
