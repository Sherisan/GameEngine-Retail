namespace Worldiator.Server.Contracts;

/// <summary>
/// RGS (Remote Gaming Server) wallet integration contract.
/// Each operator implements this interface for their wallet system.
/// The game engine only talks to wallets through this interface — no wallet logic lives elsewhere.
///
/// HOW TO INTEGRATE:
///   1. Implement IRGSAdapter (e.g. WsbRGSAdapter)
///   2. Register in Program.cs: builder.Services.AddSingleton&lt;IRGSAdapter, WsbRGSAdapter&gt;()
///   3. Configure endpoint in appsettings.json under "RGS"
/// </summary>
public interface IRGSAdapter
{
    /// <summary>Validate a player session token. Returns null if invalid or expired.</summary>
    Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken);

    /// <summary>Get the player's current wallet balance.</summary>
    Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null);

    /// <summary>
    /// Debit the player's wallet on bet placement.
    /// Must be idempotent — use reference as idempotency key.
    /// Throw RGSException on failure — the bet will not be placed.
    /// </summary>
    Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null);

    /// <summary>
    /// Credit the player's wallet on cashout or win.
    /// Must be idempotent — use reference as idempotency key.
    /// </summary>
    Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null);

    /// <summary>
    /// Rollback (refund) a debit — called when the round closes mid-placement or a bet is cancelled.
    /// Pass the debit's amount and currency: Games-API's wallet refunds exactly the amount sent.
    /// Must be idempotent — repeating it for the same debit must not refund twice.
    /// </summary>
    Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null);
}

/// <summary>Player info returned by the RGS on successful authentication.</summary>
public record RGSPlayerInfo(
    string PlayerId,
    string Username,
    string Currency,
    string SessionToken,
    decimal Balance
);
