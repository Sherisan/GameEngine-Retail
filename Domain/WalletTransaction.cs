namespace Worldiator.Server.Domain;

public enum WalletTxType { Debit, Credit, Rollback }
public enum WalletTxStatus { Pending, Completed, Failed, RolledBack }

/// <summary>
/// One wallet movement requested by a game backend. (GameId, ExternalTxId) is the idempotency key —
/// replaying the same request returns the original outcome instead of moving money twice.
/// </summary>
public class WalletTransaction
{
    public string GameId { get; set; } = string.Empty;
    /// <summary>Transaction id chosen by the game backend.</summary>
    public string ExternalTxId { get; set; } = string.Empty;
    public WalletTxType Type { get; set; }
    public WalletTxStatus Status { get; set; } = WalletTxStatus.Pending;
    public string PlayerId { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string RoundId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    /// <summary>Reference sent to the RGS adapter (its own idempotency key).</summary>
    public string Reference { get; set; } = string.Empty;
    /// <summary>For a rollback: the ExternalTxId of the debit being reversed.</summary>
    public string? RelatedExternalTxId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
