using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure.RGS;

/// <summary>
/// RGS wallet adapter for Operator 3.
///
/// OPERATOR 3 DEV — implement the five methods below using your wallet API.
/// Config lives under "RGS:Operator3" in appsettings.json / environment variables:
///   RGS__Operator3__BaseUrl        = https://wallet.operator3.com/api/
///   RGS__Operator3__ApiKey         = your-api-key
///   RGS__Operator3__TimeoutSeconds = 10
///   RGS__Operator3__MaxRetries     = 3
///
/// To activate: set RGS__Provider=Operator3 in your environment / appsettings.
/// </summary>
public class Operator3RGSAdapter : HttpRGSAdapterBase
{
    public Operator3RGSAdapter(HttpClient http, IConfiguration config, ILogger<Operator3RGSAdapter> logger)
        : base(http, config, logger, "Operator3") { }

    public override Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken)
    {
        // TODO: Validate sessionToken against your auth server.
        throw new NotImplementedException("Operator3RGSAdapter.AuthenticateAsync not implemented");
    }

    public override Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null)
    {
        // TODO: Return the player's current balance from your wallet system.
        throw new NotImplementedException("Operator3RGSAdapter.GetBalanceAsync not implemented");
    }

    public override Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        // TODO: Debit the player's wallet. Use `reference` as idempotency key.
        // Throw RGSException on rejection. Return your transaction ID.
        throw new NotImplementedException("Operator3RGSAdapter.DebitAsync not implemented");
    }

    public override Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        // TODO: Credit the player's wallet. Use `reference` as idempotency key. Return your transaction ID.
        throw new NotImplementedException("Operator3RGSAdapter.CreditAsync not implemented");
    }

    public override Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
    {
        // TODO: Reverse debit identified by `transactionId`. Must be idempotent.
        throw new NotImplementedException("Operator3RGSAdapter.RollbackAsync not implemented");
    }
}
