using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure.Persistence;

/// <summary>
/// SQL Server ledger for the multi-game wallet endpoints (table: WalletTransactions.sql).
/// BeginAsync serialises per (game, player) with sp_getapplock so the rule checks and the insert
/// are atomic across every API instance.
/// </summary>
public class SqlWalletLedger(IConfiguration config) : IWalletLedger
{
    private readonly string _connStr = config.GetConnectionString("DefaultConnection")!;

    private const string Columns =
        "GameId, ExternalTxId, Type, Status, PlayerId, Currency, RoundId, Amount, Reference, RelatedExternalTxId, Error, CreatedAt, CompletedAt";

    public async Task<LedgerBeginResult> BeginAsync(WalletTransaction tx, decimal maxWin)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        using var dbTx = conn.BeginTransaction();

        var lockResult = await conn.QuerySingleAsync<int>(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource, 'Exclusive', 'Transaction', 10000; SELECT @r;",
            new { Resource = $"wallet:{tx.GameId}:{tx.PlayerId}" }, dbTx);
        if (lockResult < 0)
            throw new TimeoutException("Timed out waiting for the wallet ledger lock.");

        var (result, write) = await WalletLedgerRules.EvaluateAsync(tx, maxWin,
            async id => (await conn.QueryAsync<Row>(
                $"SELECT {Columns} FROM dbo.WalletTransactions WHERE GameId = @GameId AND ExternalTxId = @Id",
                new { tx.GameId, Id = id }, dbTx)).FirstOrDefault()?.ToDomain(),
            async roundId => (await conn.QueryAsync<Row>(
                $"SELECT {Columns} FROM dbo.WalletTransactions WHERE GameId = @GameId AND PlayerId = @PlayerId AND RoundId = @RoundId",
                new { tx.GameId, tx.PlayerId, RoundId = roundId }, dbTx)).Select(r => r.ToDomain()).ToList());

        switch (write)
        {
            case WalletLedgerRules.WriteAction.Insert:
                await conn.ExecuteAsync(
                    $"INSERT INTO dbo.WalletTransactions ({Columns}) VALUES " +
                    "(@GameId, @ExternalTxId, @Type, @Status, @PlayerId, @Currency, @RoundId, @Amount, @Reference, @RelatedExternalTxId, @Error, @CreatedAt, @CompletedAt)",
                    Row.From(tx), dbTx);
                break;
            case WalletLedgerRules.WriteAction.ResetToPending:
                await conn.ExecuteAsync(
                    "UPDATE dbo.WalletTransactions SET Status = 'Pending', Error = NULL WHERE GameId = @GameId AND ExternalTxId = @ExternalTxId",
                    new { tx.GameId, tx.ExternalTxId }, dbTx);
                break;
        }

        dbTx.Commit();
        return result;
    }

    public Task CompleteAsync(string gameId, string externalTxId) =>
        SetStatusAsync(gameId, externalTxId, WalletTxStatus.Completed, null);

    public Task FailAsync(string gameId, string externalTxId, string error) =>
        SetStatusAsync(gameId, externalTxId, WalletTxStatus.Failed, error.Length > 500 ? error[..500] : error);

    public Task MarkRolledBackAsync(string gameId, string debitExternalTxId) =>
        SetStatusAsync(gameId, debitExternalTxId, WalletTxStatus.RolledBack, null);

    private async Task SetStatusAsync(string gameId, string txId, WalletTxStatus status, string? error)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync(
            "UPDATE dbo.WalletTransactions SET Status = @Status, Error = @Error, " +
            "CompletedAt = CASE WHEN @Status = 'Completed' THEN SYSUTCDATETIME() ELSE CompletedAt END " +
            "WHERE GameId = @GameId AND ExternalTxId = @TxId",
            new { Status = status.ToString(), Error = error, GameId = gameId, TxId = txId });
    }

    // Enums are stored as strings, matching the Rounds.Phase column convention.
    private class Row
    {
        public string GameId { get; set; } = "";
        public string ExternalTxId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string RoundId { get; set; } = "";
        public decimal Amount { get; set; }
        public string Reference { get; set; } = "";
        public string? RelatedExternalTxId { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public WalletTransaction ToDomain() => new()
        {
            GameId = GameId, ExternalTxId = ExternalTxId,
            Type = Enum.Parse<WalletTxType>(Type), Status = Enum.Parse<WalletTxStatus>(Status),
            PlayerId = PlayerId, Currency = Currency, RoundId = RoundId, Amount = Amount,
            Reference = Reference, RelatedExternalTxId = RelatedExternalTxId, Error = Error,
            CreatedAt = CreatedAt, CompletedAt = CompletedAt,
        };

        public static Row From(WalletTransaction t) => new()
        {
            GameId = t.GameId, ExternalTxId = t.ExternalTxId,
            Type = t.Type.ToString(), Status = t.Status.ToString(),
            PlayerId = t.PlayerId, Currency = t.Currency, RoundId = t.RoundId, Amount = t.Amount,
            Reference = t.Reference, RelatedExternalTxId = t.RelatedExternalTxId, Error = t.Error,
            CreatedAt = t.CreatedAt, CompletedAt = t.CompletedAt,
        };
    }
}
