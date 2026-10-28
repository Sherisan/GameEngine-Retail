namespace Worldiator.Server.Domain.MarginCall;

/// <summary>WAITING → TRADING → CLOSED → CRASHED → SETTLING → RESULTS → WAITING. Positions open only while TRADING.</summary>
public enum MarginRoundStatus { WAITING, TRADING, CLOSED, CRASHED, SETTLING, RESULTS }

public record MarginCandle(long Time, double Open, double High, double Low, double Close);

public class MarginRound
{
    public long Id { get; init; }
    public MarginRoundStatus Status { get; set; }
    /// <summary>Hidden until the crash — only sets how long the round runs, not what it pays.</summary>
    public double CrashMultiplier { get; init; }
    public long StartedAt { get; set; }
    public long PhaseEndsAt { get; set; }
    public long? CrashedAt { get; set; }
    public Dictionary<string, double>? CrashPrices { get; set; }
}

public class MarginPosition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public long RoundId { get; init; }
    public string PlayerId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string Direction { get; init; } = "BUY";
    public decimal Stake { get; init; }
    public int Multiplier { get; init; }
    /// <summary>Price the position opened at, spread included (ask for BUY, bid for SELL).</summary>
    public double EntryPrice { get; init; }
    public long OpenedAt { get; init; }
}

/// <summary>A settled position as sent to its owner.</summary>
public record MarginSettledPosition(
    string Id,
    long RoundId,
    string Market,
    string Direction,
    decimal Stake,
    int Multiplier,
    double EntryPrice,
    /// <summary>Price the position closed at, spread included (bid for BUY, ask for SELL).</summary>
    double SettlementPrice,
    decimal Commission,
    /// <summary>Net result: ReturnAmount − Stake (commission included).</summary>
    decimal Pnl,
    decimal ReturnAmount,
    string Result,
    long OpenedAt,
    long ClosedAt,
    /// <summary>True if the return was due but the wallet credit failed — it's in the ledger for reprocessing.</summary>
    bool WinPending);
