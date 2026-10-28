using Dapper;
using Microsoft.Data.SqlClient;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure.Persistence;

/// <summary>
/// SQL Server-backed round persistence via Dapper + stored procedures (see StoredProcedures.sql).
/// Used for durable storage — not for game loop reads (those stay in InMemoryRoundRepository).
/// Writes are fire-and-forget from GameHostedService; reads serve admin endpoints.
/// </summary>
public class SqlRoundRepository(IConfiguration config) : IAsyncRoundRepository
{
    private readonly string _connStr = config.GetConnectionString("DefaultConnection")!;

    public async Task SaveRoundAsync(Round round)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("dbo.usp_UpsertRound", new
        {
            round.RoundId,
            round.Nonce,
            round.ServerSeed,
            round.ServerSeedHash,
            round.ClientSeed,
            round.CrashMultiplier,
            Phase = round.Phase.ToString(),
            round.StartedAt,
            round.FlyingStartedAt,
            round.CrashedAt,
        }, tx, commandType: System.Data.CommandType.StoredProcedure);

        foreach (var bet in round.Bets)
        {
            await conn.ExecuteAsync("dbo.usp_UpsertBet", new
            {
                bet.BetId,
                bet.RoundId,
                bet.PlayerId,
                bet.Username,
                bet.SlotIndex,
                bet.Amount,
                bet.Currency,
                bet.CashedOutAt,
                bet.WinAmount,
                bet.AutoCashOutAt,
                bet.DebitTransactionId,
                bet.CreditTransactionId,
                bet.PlacedAt,
            }, tx, commandType: System.Data.CommandType.StoredProcedure);
        }

        tx.Commit();
    }

    public async Task<IReadOnlyList<Round>> GetRecentAsync(int count = 50)
    {
        using var conn = new SqlConnection(_connStr);
        using var multi = await conn.QueryMultipleAsync("dbo.usp_GetRecentRounds", new { Count = count },
            commandType: System.Data.CommandType.StoredProcedure);

        var roundRows = (await multi.ReadAsync<RoundRow>()).ToList();
        var betRows = (await multi.ReadAsync<BetRow>()).ToList();
        var betsByRound = betRows.GroupBy(b => b.RoundId).ToDictionary(g => g.Key, g => g.ToList());

        return roundRows.Select(r => MapRound(r, betsByRound.GetValueOrDefault(r.RoundId) ?? new())).ToList();
    }

    /// <summary>Return a specific player's bet history, newest first — powers the "My Bets" view.</summary>
    public async Task<IReadOnlyList<Bet>> GetBetsForPlayerAsync(string playerId, int count = 50)
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<BetRow>("dbo.usp_GetPlayerBetHistory", new { PlayerId = playerId, Count = count },
            commandType: System.Data.CommandType.StoredProcedure);
        return rows.Select(MapBet).ToList();
    }

    /// <summary>Return past round crash multipliers only (no bet join) — lighter than GetRecentAsync for a multiplier ticker.</summary>
    public async Task<IReadOnlyList<Round>> GetMultiplierHistoryAsync(int count = 50)
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<MultiplierHistoryRow>("dbo.usp_GetRoundMultiplierHistory", new { Count = count },
            commandType: System.Data.CommandType.StoredProcedure);
        return rows.Select(r => new Round
        {
            RoundId = r.RoundId,
            Nonce = r.Nonce,
            CrashMultiplier = r.CrashMultiplier,
            Phase = GamePhase.Crashed,
            CrashedAt = r.CrashedAt,
        }).ToList();
    }

    private static Round MapRound(RoundRow r, List<BetRow> bets) => new()
    {
        RoundId = r.RoundId,
        Nonce = r.Nonce,
        ServerSeed = r.ServerSeed,
        ServerSeedHash = r.ServerSeedHash,
        ClientSeed = r.ClientSeed,
        CrashMultiplier = r.CrashMultiplier,
        Phase = Enum.Parse<GamePhase>(r.Phase),
        StartedAt = r.StartedAt,
        FlyingStartedAt = r.FlyingStartedAt,
        CrashedAt = r.CrashedAt,
        Bets = bets.Select(MapBet).ToList(),
    };

    private static Bet MapBet(BetRow b) => new()
    {
        BetId = b.BetId,
        RoundId = b.RoundId,
        PlayerId = b.PlayerId,
        Username = b.Username,
        SlotIndex = b.SlotIndex,
        Amount = b.Amount,
        Currency = b.Currency,
        CashedOutAt = b.CashedOutAt,
        WinAmount = b.WinAmount,
        AutoCashOutAt = b.AutoCashOutAt,
        DebitTransactionId = b.DebitTransactionId,
        CreditTransactionId = b.CreditTransactionId,
        PlacedAt = b.PlacedAt,
    };
}

// ─── Dapper row types — mirror the DB schema, never exposed outside Infrastructure ───

public class RoundRow
{
    public string RoundId { get; set; } = string.Empty;
    public long Nonce { get; set; }
    public string ServerSeed { get; set; } = string.Empty;
    public string ServerSeedHash { get; set; } = string.Empty;
    public string ClientSeed { get; set; } = string.Empty;
    public decimal CrashMultiplier { get; set; }
    public string Phase { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FlyingStartedAt { get; set; }
    public DateTime? CrashedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BetRow
{
    public string BetId { get; set; } = string.Empty;
    public string RoundId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal? CashedOutAt { get; set; }
    public decimal? WinAmount { get; set; }
    public decimal? AutoCashOutAt { get; set; }
    public string? DebitTransactionId { get; set; }
    public string? CreditTransactionId { get; set; }
    public DateTime PlacedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MultiplierHistoryRow
{
    public string RoundId { get; set; } = string.Empty;
    public long Nonce { get; set; }
    public decimal CrashMultiplier { get; set; }
    public DateTime? CrashedAt { get; set; }
}
