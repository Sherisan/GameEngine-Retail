using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Process-local ledger for development. Lost on restart and not shared between instances —
/// production must use SqlWalletLedger (registered automatically when a SQL connection string is set).
/// </summary>
public class InMemoryWalletLedger : IWalletLedger
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(string GameId, string TxId), WalletTransaction> _txs = new();

    public async Task<LedgerBeginResult> BeginAsync(WalletTransaction tx, decimal maxWin)
    {
        await _lock.WaitAsync();
        try
        {
            var (result, write) = await WalletLedgerRules.EvaluateAsync(tx, maxWin,
                id => Task.FromResult(_txs.GetValueOrDefault((tx.GameId, id))),
                roundId => Task.FromResult<IReadOnlyList<WalletTransaction>>(_txs.Values
                    .Where(t => t.GameId == tx.GameId && t.PlayerId == tx.PlayerId && t.RoundId == roundId)
                    .ToList()));

            if (write != WalletLedgerRules.WriteAction.None)
                _txs[(tx.GameId, tx.ExternalTxId)] = tx;
            return result;
        }
        finally { _lock.Release(); }
    }

    public Task CompleteAsync(string gameId, string externalTxId) =>
        Update(gameId, externalTxId, t => { t.Status = WalletTxStatus.Completed; t.CompletedAt = DateTime.UtcNow; });

    public Task FailAsync(string gameId, string externalTxId, string error) =>
        Update(gameId, externalTxId, t => { t.Status = WalletTxStatus.Failed; t.Error = error; });

    public Task MarkRolledBackAsync(string gameId, string debitExternalTxId) =>
        Update(gameId, debitExternalTxId, t => t.Status = WalletTxStatus.RolledBack);

    private async Task Update(string gameId, string txId, Action<WalletTransaction> change)
    {
        await _lock.WaitAsync();
        try
        {
            if (_txs.TryGetValue((gameId, txId), out var tx)) change(tx);
        }
        finally { _lock.Release(); }
    }
}
