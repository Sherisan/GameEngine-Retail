namespace Worldiator.Server.Domain.Retail;

/// <summary>Bound from the "Retail" config section.</summary>
public class RetailOptions
{
    /// <summary>Currency of every EGM unless the EGM overrides it.</summary>
    public string Currency { get; set; } = "ZAR";

    /// <summary>Game an EGM session plays when the launch doesn't name one.</summary>
    public string GameId { get; set; } = "feeding-frenzy";

    /// <summary>Shared secret the terminal sends in X-Terminal-Key. An EGM's own TerminalKey replaces it for that EGM.</summary>
    public string TerminalApiKey { get; set; } = string.Empty;

    /// <summary>Development only: accept any egmid without an Egms entry. Never enable with real money.</summary>
    public bool AllowUnregisteredEgms { get; set; }

    public List<RetailEgm> Egms { get; set; } = new();
}

/// <summary>One cabinet allowed to launch the game and send terminal events.</summary>
public class RetailEgm
{
    public string EgmId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Currency { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>When set, only these client IPs may open a game session for this EGM (the cabinet's own address).</summary>
    public List<string> AllowedIps { get; set; } = new();

    /// <summary>When set, the terminal must send this key (instead of Retail:TerminalApiKey) for this EGM.</summary>
    public string? TerminalKey { get; set; }
}

/// <summary>What a call to the EGM wallet endpoint is for. Debit and credit amounts carry the money; this labels it.</summary>
public static class EgmWalletType
{
    public const string Balance  = "Balance";   // debit 0, credit 0 — read only
    public const string Debit    = "Debit";     // a bet
    public const string Credit   = "Credit";    // a win
    public const string Rollback = "Rollback";  // a bet refunded (credit = the stake)
}

/// <summary>Body of POST {EGM API}/wallet — the one endpoint the EGM hosts for this API.</summary>
public record EgmWalletRequest(
    string EgmId,
    decimal Debit,
    decimal Credit,
    string Type,
    string? TransactionId,
    string? RoundId,
    string? GameId,
    string Currency,
    /// <summary>For a Rollback: the TransactionId of the debit being refunded.</summary>
    string? RelatedTransactionId = null);

/// <summary>The EGM's answer. Balance is the meter after the call.</summary>
public record EgmWalletResponse(
    string? EgmId,
    decimal? Balance,
    string? TransactionId,
    bool Duplicate = false,
    string? Error = null,
    string? Message = null);
