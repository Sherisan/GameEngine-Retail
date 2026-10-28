using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure.RGS;

/// <summary>
/// RGS wallet adapter for Operator 2.
///
/// OPERATOR 2 DEV — implement the five methods below using your wallet API.
/// Config lives under "RGS:Operator2" in appsettings.json / environment variables:
///   RGS__Operator2__BaseUrl        = https://wallet.operator2.com/api/
///   RGS__Operator2__ApiKey         = your-api-key
///   RGS__Operator2__TimeoutSeconds = 10
///   RGS__Operator2__MaxRetries     = 3
///
/// To activate: set RGS__Provider=Operator2 in your environment / appsettings.
///
/// NOTE: If your API uses SOAP/XML instead of JSON, you can use Http.PostAsync with
/// StringContent("...", Encoding.UTF8, "text/xml") — the base class HttpClient is protocol-agnostic.
/// </summary>
public class Operator2RGSAdapter : HttpRGSAdapterBase
{
    public Operator2RGSAdapter(HttpClient http, IConfiguration config, ILogger<Operator2RGSAdapter> logger)
        : base(http, config, logger, "Operator2") { }

    public override Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken)
    {
        // TODO: Validate sessionToken against your auth server.
        throw new NotImplementedException("Operator2RGSAdapter.AuthenticateAsync not implemented");
    }

    public override Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null)
    {
        // TODO: Return the player's current balance from your wallet system.
        throw new NotImplementedException("Operator2RGSAdapter.GetBalanceAsync not implemented");
    }

    public override Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        // TODO: Debit the player's wallet. Use `reference` as idempotency key.
        // Throw RGSException on rejection. Return your transaction ID.
        throw new NotImplementedException("Operator2RGSAdapter.DebitAsync not implemented");
    }

    public override Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        // TODO: Credit the player's wallet. Use `reference` as idempotency key. Return your transaction ID.
        throw new NotImplementedException("Operator2RGSAdapter.CreditAsync not implemented");
    }

    public override Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
    {
        // TODO: Reverse debit identified by `transactionId`. Must be idempotent.
        throw new NotImplementedException("Operator2RGSAdapter.RollbackAsync not implemented");
    }
}
