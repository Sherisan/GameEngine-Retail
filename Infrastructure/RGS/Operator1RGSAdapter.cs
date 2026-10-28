using System.Net.Http.Json;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure.RGS;

/// <summary>
/// RGS wallet adapter for Operator 1.
///
/// OPERATOR 1 DEV — implement the five methods below using your wallet API.
/// Config lives under "RGS:Operator1" in appsettings.json / environment variables:
///   RGS__Operator1__BaseUrl   = https://wallet.operator1.com/api/
///   RGS__Operator1__ApiKey    = your-api-key
///   RGS__Operator1__TimeoutSeconds = 10
///   RGS__Operator1__MaxRetries     = 3
///
/// To activate: set RGS__Provider=Operator1 in your environment / appsettings.
/// </summary>
public class Operator1RGSAdapter : HttpRGSAdapterBase
{
    public Operator1RGSAdapter(HttpClient http, IConfiguration config, ILogger<Operator1RGSAdapter> logger)
        : base(http, config, logger, "Operator1") { }

    public override Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken)
    {
        // TODO: Call your session-validation endpoint.
        // Expected: validate sessionToken with your auth server, return player info on success or null if invalid.
        //
        // Example:
        //   return RetryAsync(async () =>
        //   {
        //       var res = await Http.GetAsync($"sessions/{sessionToken}");
        //       if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        //       res.EnsureSuccessStatusCode();
        //       var body = await res.Content.ReadFromJsonAsync<YourSessionResponse>();
        //       return new RGSPlayerInfo(body.PlayerId, body.Username, body.Currency, sessionToken, body.Balance);
        //   }, nameof(AuthenticateAsync));
        throw new NotImplementedException("Operator1RGSAdapter.AuthenticateAsync not implemented");
    }

    public override Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null)
    {
        // TODO: Call your balance endpoint and return the player's current balance.
        throw new NotImplementedException("Operator1RGSAdapter.GetBalanceAsync not implemented");
    }

    public override Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        // TODO: Debit the player's wallet.
        // - Use `reference` as your idempotency key so duplicate calls don't double-charge.
        // - Throw RGSException("Insufficient funds", "INSUFFICIENT_FUNDS") if balance is too low.
        // - Throw RGSException(message, code) for other wallet rejections.
        // - Return the transaction ID issued by your system.
        throw new NotImplementedException("Operator1RGSAdapter.DebitAsync not implemented");
    }

    public override Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        // TODO: Credit the player's wallet on cashout/win.
        // - Use `reference` as your idempotency key.
        // - Return the transaction ID issued by your system.
        throw new NotImplementedException("Operator1RGSAdapter.CreditAsync not implemented");
    }

    public override Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
    {
        // TODO: Reverse a debit identified by `transactionId`.
        // Called when a bet is placed but the round closes before we can accept it.
        // This MUST succeed or the player's wallet will be incorrectly debited — ensure your endpoint is idempotent.
        throw new NotImplementedException("Operator1RGSAdapter.RollbackAsync not implemented");
    }
}
