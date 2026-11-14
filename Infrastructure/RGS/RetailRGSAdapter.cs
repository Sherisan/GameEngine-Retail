using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Worldiator.Server.Application.Retail;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Retail;

namespace Worldiator.Server.Infrastructure.RGS;

/// <summary>
/// Wallet for retail cabinets. The EGM holds the money; the player id is the EGM id. Every balance read, bet,
/// win and refund is one call to the EGM's wallet endpoint:
///   POST {RGS:Retail:BaseUrl}{RGS:Retail:WalletPath}   X-Api-Key: {RGS:Retail:ApiKey}
///   { egmId, debit, credit, type, transactionId, roundId, gameId, currency, relatedTransactionId }
///   → { egmId, balance, transactionId, duplicate }
/// Debit and credit both 0 reads the balance. The EGM must treat transactionId as an idempotency key — a timed-out
/// call is retried with the same id. Contract: docs/RETAIL-TERMINAL-INTEGRATION.md.
/// Activate with RGS:Provider = Retail.
/// </summary>
public class RetailRGSAdapter : HttpRGSAdapterBase
{
    private readonly RetailWalletService _retail;
    private readonly string _walletPath;

    public RetailRGSAdapter(HttpClient http, IConfiguration config, ILogger<RetailRGSAdapter> logger, RetailWalletService retail)
        : base(http, config, logger, "Retail")
    {
        _retail     = retail;
        _walletPath = (config["RGS:Retail:WalletPath"] ?? "wallet").TrimStart('/');
        if (!string.IsNullOrEmpty(Options.ApiKey))
            Http.DefaultRequestHeaders.Add("X-Api-Key", Options.ApiKey);
    }

    public override async Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken)
    {
        try
        {
            var egm = _retail.FindEgm(new JwtSecurityTokenHandler().ReadJwtToken(sessionToken).Subject);
            if (egm == null) return null;
            return new RGSPlayerInfo(egm.EgmId, egm.Name ?? egm.EgmId, _retail.CurrencyOf(egm), sessionToken,
                await GetBalanceAsync(egm.EgmId, _retail.CurrencyOf(egm)));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "RetailRGSAdapter.AuthenticateAsync failed");
            return null;
        }
    }

    public override async Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null) =>
        (await CallAsync(new EgmWalletRequest(playerId, 0, 0, EgmWalletType.Balance, null, null, gameId,
            _retail.CurrencyOf(playerId)))).Balance!.Value;

    public override async Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        await CallAsync(new EgmWalletRequest(playerId, amount, 0, EgmWalletType.Debit, reference, roundId, gameId,
            _retail.CurrencyOf(playerId)), push: true);
        return reference;
    }

    public override async Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        await CallAsync(new EgmWalletRequest(playerId, 0, amount, EgmWalletType.Credit, reference, roundId, gameId,
            _retail.CurrencyOf(playerId)), push: true);
        return reference;
    }

    // transactionId is what DebitAsync returned — the debit's own reference. The refund id is derived from it,
    // so a retried rollback reaches the EGM with the same id and is never paid twice.
    public override async Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
    {
        if (amount is null or <= 0)
            throw new InvalidOperationException("RollbackAsync needs the debit amount — the EGM refunds exactly the credit sent.");
        await CallAsync(new EgmWalletRequest(playerId, 0, amount.Value, EgmWalletType.Rollback, "ROLLBACK_" + transactionId,
            roundId, gameId, _retail.CurrencyOf(playerId), RelatedTransactionId: transactionId), push: true);
    }

    /// <summary>
    /// One call to the EGM wallet. 200 = done (a repeat of a booked transactionId also answers 200, duplicate=true).
    /// 4xx = the EGM refused it → RGSException, not retried. 5xx / timeout = outcome unknown → retried with the same id.
    /// </summary>
    private async Task<EgmWalletResponse> CallAsync(EgmWalletRequest req, bool push = false)
    {
        var body = await RetryAsync(async () =>
        {
            using var res = await Http.PostAsJsonAsync(_walletPath, req);
            var parsed = await ReadAsync(res);

            if (res.IsSuccessStatusCode)
            {
                if (parsed?.Balance is null)
                    throw new InvalidOperationException($"EGM wallet {req.Type} for {req.EgmId} answered {(int)res.StatusCode} without a balance.");
                return parsed;
            }
            if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException($"EGM wallet {req.Type} for {req.EgmId} failed: {(int)res.StatusCode} {parsed?.Error}");

            throw (parsed?.Error, res.StatusCode) switch
            {
                ("INSUFFICIENT_FUNDS", _) or (_, HttpStatusCode.PaymentRequired) =>
                    new RGSException("Insufficient credits — insert a note or transfer funds to play.", "INSUFFICIENT_FUNDS"),
                ("EGM_LOCKED", _) or (_, HttpStatusCode.Locked) =>
                    new RGSException("A cash-out is in progress on this machine.", "EGM_LOCKED"),
                ("EGM_NOT_FOUND", _) =>
                    new RGSException("This machine isn't known to the wallet.", "EGM_NOT_FOUND"),
                _ => new RGSException(parsed?.Message ?? $"The EGM wallet refused the {req.Type.ToLowerInvariant()} ({(int)res.StatusCode}).",
                    parsed?.Error ?? "WALLET_REJECTED"),
            };
        }, $"EgmWallet.{req.Type}");

        if (push)
            await _retail.PushBalanceAsync(req.EgmId, body.Balance!.Value, req.Type);
        return body;
    }

    private static async Task<EgmWalletResponse?> ReadAsync(HttpResponseMessage res)
    {
        try { return await res.Content.ReadFromJsonAsync<EgmWalletResponse>(); }
        catch (Exception) { return null; }
    }
}
