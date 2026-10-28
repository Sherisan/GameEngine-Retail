using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure.RGS;

/// <summary>
/// Base class for HTTP-based RGS wallet adapters.
///
/// OPERATOR INTEGRATION GUIDE:
///   1. Create a class that extends HttpRGSAdapterBase (e.g. "WsbRGSAdapter").
///   2. Pass your config section key (e.g. "Wsb") to the base constructor.
///   3. Implement the five abstract methods using this.Http and this.Options.
///   4. Register in Program.cs via the "RGS:Provider" config key (see Program.cs).
///   5. Add your config block to appsettings.json under "RGS:{YourKey}".
///
/// Built-in features you get for free:
///   - Typed HttpClient with base URL and timeout configured from appsettings
///   - Exponential-backoff retry on transient HTTP/network failures
///   - Structured logging on every retry attempt
/// </summary>
public abstract class HttpRGSAdapterBase : IRGSAdapter
{
    protected readonly HttpClient Http;
    protected readonly RGSAdapterOptions Options;
    protected readonly ILogger Logger;

    protected HttpRGSAdapterBase(HttpClient http, IConfiguration config, ILogger logger, string sectionKey)
    {
        Http    = http;
        Logger  = logger;
        Options = config.GetSection($"RGS:{sectionKey}").Get<RGSAdapterOptions>()
                  ?? throw new InvalidOperationException(
                      $"Missing config section \"RGS:{sectionKey}\". Add it to appsettings.json or environment variables.");

        if (string.IsNullOrWhiteSpace(Options.BaseUrl))
            throw new InvalidOperationException($"RGS:{sectionKey}:BaseUrl must be set.");

        Http.BaseAddress = new Uri(Options.BaseUrl.TrimEnd('/') + '/');
        Http.Timeout     = TimeSpan.FromSeconds(Options.TimeoutSeconds);
    }

    /// <summary>
    /// Executes an async operation with exponential-backoff retries on transient failures.
    /// Throws on the final attempt or on non-transient errors.
    /// </summary>
    protected async Task<T> RetryAsync<T>(Func<Task<T>> operation, string operationName)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < Options.MaxRetries - 1 && IsTransient(ex))
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Logger.LogWarning(
                    "RGS {Operation} transient failure (attempt {Attempt}/{Max}), retrying in {Delay}s: {Message}",
                    operationName, attempt, Options.MaxRetries, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay);
            }
        }
    }

    /// <summary>Overload for void operations.</summary>
    protected async Task RetryAsync(Func<Task> operation, string operationName)
    {
        await RetryAsync(async () => { await operation(); return true; }, operationName);
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;

    public abstract Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken);
    public abstract Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null);
    public abstract Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null);
    public abstract Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null);
    public abstract Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null);
}
