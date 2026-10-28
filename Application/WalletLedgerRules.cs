using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

/// <summary>
/// Ledger rules shared by every IWalletLedger implementation. Callers must hold a lock covering
/// (GameId, PlayerId) for the whole evaluate-then-write, so concurrent requests can't both pass a check.
/// </summary>
public static class WalletLedgerRules
{
    public enum WriteAction { None, Insert, ResetToPending }

    public static async Task<(LedgerBeginResult Result, WriteAction Write)> EvaluateAsync(
        WalletTransaction tx,
        decimal maxWin,
        Func<string, Task<WalletTransaction?>> findByTxId,
        Func<string, Task<IReadOnlyList<WalletTransaction>>> listRound)
    {
        // A rollback inherits round and amount from the debit it reverses.
        WalletTransaction? debit = null;
        if (tx.Type == WalletTxType.Rollback)
        {
            debit = await findByTxId(tx.RelatedExternalTxId ?? string.Empty);
            if (debit == null || debit.Type != WalletTxType.Debit || debit.PlayerId != tx.PlayerId)
                return Reject(tx, "DEBIT_NOT_FOUND", "No debit with that transaction id for this player.");
            tx.RoundId = debit.RoundId;
            tx.Amount  = debit.Amount;
        }

        var existing = await findByTxId(tx.ExternalTxId);
        var write = WriteAction.Insert;
        if (existing != null)
        {
            if (existing.Type != tx.Type || existing.PlayerId != tx.PlayerId || existing.RoundId != tx.RoundId
                || existing.Amount != tx.Amount || existing.RelatedExternalTxId != tx.RelatedExternalTxId)
                return Reject(existing, "TX_ID_REUSED", "Transaction id was already used for a different request.");

            switch (existing.Status)
            {
                case WalletTxStatus.Completed:
                    return (new LedgerBeginResult(LedgerOutcome.AlreadyCompleted, existing), WriteAction.None);
                case WalletTxStatus.RolledBack:
                    return Reject(existing, "ALREADY_ROLLED_BACK", "That debit was rolled back.");
                case WalletTxStatus.Pending:
                    return (new LedgerBeginResult(LedgerOutcome.InProgress, existing, "IN_PROGRESS", "Transaction is still being processed."), WriteAction.None);
            }
            // Failed: retry with the same reference (the RGS is idempotent on it), but re-check the rules —
            // the round may have changed since the first attempt.
            tx.Reference = existing.Reference;
            tx.CreatedAt = existing.CreatedAt;
            write = WriteAction.ResetToPending;
        }

        var round = (await listRound(tx.RoundId)).Where(t => t.ExternalTxId != tx.ExternalTxId).ToList();
        bool Live(WalletTransaction t) => t.Status is WalletTxStatus.Pending or WalletTxStatus.Completed;

        switch (tx.Type)
        {
            case WalletTxType.Credit:
                if (!round.Any(t => t.Type == WalletTxType.Debit && t.Status == WalletTxStatus.Completed))
                    return Reject(tx, "NO_DEBIT", "No completed debit for this round — nothing to settle.");
                var credited = round.Where(t => t.Type == WalletTxType.Credit && Live(t)).Sum(t => t.Amount);
                if (credited + tx.Amount > maxWin)
                    return Reject(tx, "MAX_WIN_EXCEEDED", FormattableString.Invariant($"Credits for this round would exceed the max win of {maxWin}."));
                break;

            case WalletTxType.Rollback:
                if (debit!.Status == WalletTxStatus.RolledBack)
                    return Reject(tx, "ALREADY_ROLLED_BACK", "That debit was already rolled back.");
                if (debit.Status == WalletTxStatus.Pending)
                    return Reject(tx, "DEBIT_IN_PROGRESS", "That debit is still being processed.");
                if (round.Any(t => t.Type == WalletTxType.Rollback && t.RelatedExternalTxId == debit.ExternalTxId && Live(t)))
                    return Reject(tx, "ROLLBACK_IN_PROGRESS", "Another rollback for that debit is in progress.");
                if (round.Any(t => t.Type == WalletTxType.Credit && Live(t)))
                    return Reject(tx, "ROUND_SETTLED", "The round has already been credited and can't be rolled back.");
                break;
        }

        tx.Status = WalletTxStatus.Pending;
        tx.Error  = null;
        return (new LedgerBeginResult(LedgerOutcome.Started, tx), write);
    }

    private static (LedgerBeginResult, WriteAction) Reject(WalletTransaction tx, string code, string message) =>
        (new LedgerBeginResult(LedgerOutcome.Rejected, tx, code, message), WriteAction.None);
}
