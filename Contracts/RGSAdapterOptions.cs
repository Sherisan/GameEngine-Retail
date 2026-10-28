namespace Worldiator.Server.Contracts;

/// <summary>
/// Configuration options for an HTTP-based RGS adapter.
/// Bind from appsettings.json under "RGS:{ProviderName}" (e.g. "RGS:Wsb").
/// </summary>
public class RGSAdapterOptions
{
    /// <summary>Base URL of the operator's RGS wallet API (e.g. "https://wallet.operator.com/api/").</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>API key or shared secret issued by the operator.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Number of retry attempts on transient failures before giving up.</summary>
    public int MaxRetries { get; init; } = 3;
}
