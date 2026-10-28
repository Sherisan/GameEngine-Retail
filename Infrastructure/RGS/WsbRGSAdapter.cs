using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure.RGS;

/// <summary>
/// RGS wallet adapter for WSB platform via Games-API.
///
/// Config under "RGS:Wsb" in appsettings.json:
///   RGS__Wsb__BaseUrl   = https://games-api.yourdomain.com/
///   RGS__Wsb__ApiKey    = shared-api-key
///   RGS__Wsb__TimeoutSeconds = 10
///   RGS__Wsb__MaxRetries     = 3
///
/// Also required:
///   GamesApi__ApiKey = same shared-api-key (used by AuthController to validate inbound requests)
///
/// Activate: set RGS__Provider=Wsb in environment / appsettings.
/// </summary>
public class WsbRGSAdapter : HttpRGSAdapterBase
{
    private readonly IGameRegistry _games;

    public WsbRGSAdapter(HttpClient http, IConfiguration config, ILogger<WsbRGSAdapter> logger, IGameRegistry games)
        : base(http, config, logger, "Wsb")
    {
        _games = games;
        Http.DefaultRequestHeaders.Add("X-API-Key", Options.ApiKey);
    }

    /// <summary>
    /// JWT is already validated by ASP.NET Core middleware before any hub method runs.
    /// This method parses claims from the token to build RGSPlayerInfo, then fetches live balance.
    /// </summary>
    public override async Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt     = handler.ReadJwtToken(sessionToken);

            var playerId = jwt.Claims.FirstOrDefault(c => c.Type is JwtRegisteredClaimNames.Sub or "sub")?.Value;
            var username = jwt.Claims.FirstOrDefault(c => c.Type is JwtRegisteredClaimNames.Name or "name")?.Value ?? "Player";
            var currency = jwt.Claims.FirstOrDefault(c => c.Type == "currency")?.Value ?? "ZAR";

            if (string.IsNullOrEmpty(playerId)) return null;

            var balance = await GetBalanceAsync(playerId, currency);
            return new RGSPlayerInfo(playerId, username, currency, sessionToken, balance);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "WsbRGSAdapter.AuthenticateAsync failed");
            return null;
        }
    }

    public override async Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null)
    {
        return await RetryAsync(async () =>
        {
            var res = await Http.PostAsJsonAsync("api/SdGaming/GetBalance", new { PlayerId = long.Parse(playerId), GameId = ProviderGameId(gameId) });
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadFromJsonAsync<BalanceEnvelope>();
            // Games-API answers HTTP 200 with baseResponse.isError on failure (e.g. GAME_NOT_FOUND) — never show that as a 0 balance.
            if (body?.balanceResponse == null || body.baseResponse?.isError == true)
                throw new InvalidOperationException(
                    $"GetBalance failed: {body?.baseResponse?.StatusCode} {body?.baseResponse?.StatusMessage ?? "no balance in response"}");
            return (decimal)body.balanceResponse.Balance;
        }, nameof(GetBalanceAsync));
    }

    public override async Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        return await RetryAsync(async () =>
        {
            var req = new
            {
                PlayerId      = long.Parse(playerId),
                TransactionId = GuidFromReference(reference),
                CorrelationId = (Guid?)null,
                Stake         = amount,
                RoundId       = roundId,
                CurrencyCode  = currency,
                GameId        = ProviderGameId(gameId),
            };
            var res = await Http.PostAsJsonAsync("api/SdGaming/SubmitBet", req);
            res.EnsureSuccessStatusCode();
            await EnsureWalletSuccessAsync(res, nameof(DebitAsync));
            return reference;
        }, nameof(DebitAsync));
    }

    public override async Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        return await RetryAsync(async () =>
        {
            var req = new
            {
                PlayerId      = long.Parse(playerId),
                TransactionId = GuidFromReference(reference),
                CorrelationId = (Guid?)null,
                Stake         = amount,
                RoundId       = roundId,
                CurrencyCode  = currency,
                GameId        = ProviderGameId(gameId),
            };
            var res = await Http.PostAsJsonAsync("api/SdGaming/ClaimBet", req);
            res.EnsureSuccessStatusCode();
            await EnsureWalletSuccessAsync(res, nameof(CreditAsync));
            return reference;
        }, nameof(CreditAsync));
    }

    public override async Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
    {
        // The shared wallet's Refund credits exactly the amount sent, so the debit's stake must be passed —
        // a 0 refunds nothing. Currency may be empty: Games-API then reads it from the original bet.
        if (amount is null or <= 0)
            throw new InvalidOperationException("RollbackAsync needs the debit amount — the wallet refunds exactly the amount sent.");

        await RetryAsync(async () =>
        {
            var req = new
            {
                PlayerId      = long.Parse(playerId),
                // Derived from the debit, so a retried refund reuses the same id and the wallet dedupes it (409).
                TransactionId = GuidFromReference("ROLLBACK_" + transactionId.Split('_')[^1]),
                CorrelationId = (Guid?)GuidFromReference(transactionId),
                Stake         = amount.Value,
                RoundId       = roundId,
                CurrencyCode  = currency ?? string.Empty,
                GameId        = ProviderGameId(gameId),
            };
            var res = await Http.PostAsJsonAsync("api/SdGaming/Cancel", req);
            res.EnsureSuccessStatusCode();
            await EnsureWalletSuccessAsync(res, nameof(RollbackAsync));
        }, nameof(RollbackAsync));
    }

    // Games-API's SdGaming resolves the title (GameID / GameName / GameEnumId) from this: the game's numeric
    // Game.ProviderGameID in the platform catalogue (GameDefinition.ProviderGameId). Feeding Frenzy sends nothing
    // so Games-API keeps its configured FeedingFranzee defaults.
    private string? ProviderGameId(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId) || gameId == SessionTokenService.DefaultGameId) return null;
        var id = _games.Find(gameId)?.ProviderGameId
            ?? throw new RGSException($"Game \"{gameId}\" has no ProviderGameId — add its catalogue number to the Games config.", "GAME_NOT_CONFIGURED");
        return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // reference format: "BET_<hexguid>", "WIN_<hexguid>", "GAMBLE_WIN_<hexguid>", etc.
    // BetId is Guid.ToString("N") — 32 hex chars, parseable by Guid.Parse.
    // Prefix is mixed into the GUID bytes so BET_X and WIN_X produce distinct TransactionIds
    // even though they share the same underlying betId.
    private static Guid GuidFromReference(string reference)
    {
        var parts = reference.Split('_');
        var bytes = Guid.Parse(parts[^1]).ToByteArray();

        if (parts.Length > 1)
        {
            var prefix = string.Join("_", parts, 0, parts.Length - 1);
            var seed = 0;
            foreach (var c in prefix) seed = seed * 31 + c;
            bytes[0] ^= (byte)(seed         & 0xFF);
            bytes[1] ^= (byte)((seed >>  8) & 0xFF);
            bytes[2] ^= (byte)((seed >> 16) & 0xFF);
            bytes[3] ^= (byte)((seed >> 24) & 0xFF);
        }

        return new Guid(bytes);
    }

    // Games-API's SubmitBet/ClaimBet/Cancel answer HTTP 200 and encode the wallet result in Code
    // (SdGaming.GetStatusResponse):
    //   200/201 success · 409 DUPLICATE_TRANSACTION = this id already landed, i.e. a retry of a success
    //   400/401/402/403/404 = the wallet refused it → RGSException (a player-facing rejection, not retried)
    //   500 / anything else = outcome unknown → InvalidOperationException (caller retries with the same id)
    private async Task EnsureWalletSuccessAsync(HttpResponseMessage res, string operation)
    {
        var body = await res.Content.ReadFromJsonAsync<BetResponseEnvelope>();
        if (body is null) return;
        switch (body.Code)
        {
            case 200 or 201 or 409:
                return;
            case 402:
                throw new RGSException("Your account is locked.", "PLAYER_LOCKED");
            case 403:
                throw new RGSException("Insufficient balance.", "INSUFFICIENT_FUNDS");
            case 404 when operation == nameof(RollbackAsync):
                // Nothing to refund: the debit never landed at the wallet.
                Logger.LogWarning("Rollback: wallet has no matching debit (404) — treating as already refunded");
                return;
            case 400 or 401 or 404:
                throw new RGSException($"The wallet rejected the {operation[..^5].ToLowerInvariant()} ({body.Error}).", body.Error ?? "WALLET_REJECTED");
            default:
                throw new InvalidOperationException($"{operation} wallet error {body.Code}: {body.Error}");
        }
    }

    // Minimal shape matching Games-API BalanceResponse JSON
    private record BalanceEnvelope(BalanceResDto? balanceResponse, BaseResponseDto? baseResponse);
    private record BalanceResDto(double Balance, string? CurrencyCode, string? OriginPlayerID);
    private record BaseResponseDto(bool isError, int StatusCode, string? StatusMessage);
    private record BetResponseEnvelope(int Code, string? Error);
}
