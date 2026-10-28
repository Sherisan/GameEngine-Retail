using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

public enum LedgerOutcome
{
    /// <summary>Recorded as Pending — the caller must now hit the RGS and Complete/Fail it.</summary>
    Started,
    /// <summary>Same id already completed — replay the original result, don't touch the RGS.</summary>
    AlreadyCompleted,
    /// <summary>Same id is still pending on another request.</summary>
    InProgress,
    /// <summary>Breaks a ledger rule (see ErrorCode).</summary>
    Rejected,
}

public record LedgerBeginResult(LedgerOutcome Outcome, WalletTransaction Transaction, string? ErrorCode = null, string? Error = null);

/// <summary>
/// Records wallet movements requested by game backends and enforces the rules the RGS can't:
///   - idempotency on (GameId, ExternalTxId)
///   - a credit needs a completed debit for the same game/player/round
///   - credits for one round never exceed the game's MaxWin
///   - a debit can only be rolled back once, and not after its round has been credited
/// BeginAsync must check and insert atomically.
/// </summary>
public interface IWalletLedger
{
    /// <summary>For rollbacks, tx.RelatedExternalTxId names the debit; RoundId/Amount are filled from it.</summary>
    Task<LedgerBeginResult> BeginAsync(WalletTransaction tx, decimal maxWin);
    Task CompleteAsync(string gameId, string externalTxId);
    Task FailAsync(string gameId, string externalTxId, string error);
    /// <summary>Marks the debit as reversed once its rollback has completed.</summary>
    Task MarkRolledBackAsync(string gameId, string debitExternalTxId);
}
