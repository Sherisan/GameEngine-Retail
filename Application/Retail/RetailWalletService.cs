using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Worldiator.Server.Domain.Retail;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Application.Retail;

/// <summary>
/// Retail (land-based EGM) plumbing. The EGM holds the money: this API reads the balance and books every bet
/// and win on the EGM's own wallet endpoint (RetailRGSAdapter). The EGM in turn tells this API about money
/// that arrives or leaves at the cabinet (api/retail/terminal), so the open game can show it.
/// Here: which EGMs may play, the terminal key check, and pushing balances to the cabinet's game.
/// The EGM id is the player id, so the game session, the wallet calls and the hub user are all keyed by it.
/// </summary>
public class RetailWalletService
{
    public const string TerminalKeyHeader = "X-Terminal-Key";

    private readonly IOptionsMonitor<RetailOptions> _options;
    private readonly IHubContext<GameHub> _gameHub;
    private readonly ILogger<RetailWalletService> _logger;

    public RetailWalletService(IOptionsMonitor<RetailOptions> options, IConfiguration config,
        IHubContext<GameHub> gameHub, ILogger<RetailWalletService> logger)
    {
        _options = options;
        _gameHub = gameHub;
        _logger  = logger;
        Enabled  = string.Equals(config["RGS:Provider"], "Retail", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The retail endpoints only answer when RGS:Provider = Retail.</summary>
    public bool Enabled { get; }

    public RetailOptions Options => _options.CurrentValue;

    /// <summary>The configured EGM (canonical id spelling), or null when it is unknown or disabled.</summary>
    public RetailEgm? FindEgm(string? egmId)
    {
        if (string.IsNullOrWhiteSpace(egmId)) return null;
        egmId = egmId.Trim();
        var egm = Options.Egms.FirstOrDefault(e => string.Equals(e.EgmId, egmId, StringComparison.OrdinalIgnoreCase));
        if (egm != null) return egm.Enabled ? egm : null;
        if (!Options.AllowUnregisteredEgms || egmId.Length > 50) return null;
        return new RetailEgm { EgmId = egmId };
    }

    public string CurrencyOf(RetailEgm egm) => string.IsNullOrWhiteSpace(egm.Currency) ? Options.Currency : egm.Currency;

    public string CurrencyOf(string egmId) => FindEgm(egmId) is { } egm ? CurrencyOf(egm) : Options.Currency;

    /// <summary>The cabinet may open a game session from this address (always true when it has no AllowedIps).</summary>
    public static bool IsAllowedClient(RetailEgm egm, IPAddress? remote)
    {
        if (egm.AllowedIps.Count == 0) return true;
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return egm.AllowedIps.Any(ip => IPAddress.TryParse(ip, out var allowed) && allowed.Equals(remote));
    }

    /// <summary>Checks X-Terminal-Key for this EGM: its own TerminalKey when set, otherwise Retail:TerminalApiKey.</summary>
    public bool IsValidTerminalKey(RetailEgm egm, string? key)
    {
        var expected = string.IsNullOrEmpty(egm.TerminalKey) ? Options.TerminalApiKey : egm.TerminalKey;
        return FixedEquals(expected, key);
    }

    /// <summary>True when the key belongs to the terminal at all — used to give terminals their own rate-limit bucket.</summary>
    public bool IsAnyTerminalKey(string? key) =>
        !string.IsNullOrEmpty(key)
        && (FixedEquals(Options.TerminalApiKey, key) || Options.Egms.Any(e => FixedEquals(e.TerminalKey, key)));

    /// <summary>
    /// Shows a new balance on the cabinet's open game. The game also gets the balance in its own bet / cash-out
    /// replies; this covers what it didn't start — a note inserted, an AFT transfer, a cash-out at the cabinet,
    /// and server-side auto cash-outs.
    /// </summary>
    public async Task PushBalanceAsync(string egmId, decimal balance, string reason)
    {
        try
        {
            await _gameHub.Clients.User(egmId).SendAsync("Balance", new { balance, reason });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push the retail balance to {EgmId}'s game", egmId);
        }
    }

    private static bool FixedEquals(string? expected, string? actual) =>
        !string.IsNullOrEmpty(expected) && actual != null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
