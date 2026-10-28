namespace Worldiator.Server.Domain.MarginCall;

/// <summary>
/// Margin Call (TradeX) — trading-themed crash game. Bound from the "MarginCall" section.
///
/// House edge comes from two knobs, usable together or alone:
///   CommissionPercent — % of every stake kept at settlement (same edge on every market / multiplier).
///   SpreadBps         — BUY opens at ask and closes at bid, SELL the reverse (edge scales with the multiplier).
/// Prices are a zero-drift random walk (a martingale), so with no commission and no spread RTP is exactly 100%;
/// the defaults (3% + 10 bps) measure 96–97% across all markets and multipliers.
/// </summary>
public class MarginCallOptions
{
    /// <summary>The "Games" entry this engine plays (wallet limits, session tokens, SdGaming title).</summary>
    public string GameId { get; set; } = "tradex";

    public decimal CommissionPercent { get; set; } = 3.0m;
    public double SpreadBps { get; set; } = 10;

    /// <summary>
    /// Risk multipliers players may choose (P/L = stake × multiplier × price move, max loss = stake).
    /// No default here — config binding appends to an array's default values, which would duplicate them.
    /// </summary>
    public int[] Multipliers { get; set; } = [];

    /// <summary>The configured multipliers, or 1/2/5/10 when none are set.</summary>
    public int[] AllowedMultipliers => Multipliers.Length > 0 ? Multipliers.Distinct().Order().ToArray() : [1, 2, 5, 10];

    /// <summary>Most open positions one player may hold in a round.</summary>
    public int MaxPositionsPerRound { get; set; } = 20;

    public int TickMs { get; set; } = 90;

    // Round timing — mirrors the TradeX frontend's original GAME constants.
    public double GrowthRate { get; set; } = 0.075;
    public int WaitingMs { get; set; } = 4200;
    public int ClosedMs { get; set; } = 2400;
    public int MinOpenMs { get; set; } = 4200;
    public int MaxTradingMs { get; set; } = 42000;
    public int CrashedMs { get; set; } = 850;
    public int SettlingMs { get; set; } = 950;
    public int ResultsMs { get; set; } = 5400;

    public List<MarginMarketConfig> Markets { get; set; } = [];
}

public class MarginMarketConfig
{
    public string Symbol { get; set; } = string.Empty;
    public double BasePrice { get; set; }
    /// <summary>Per-tick standard deviation as a fraction of price.</summary>
    public double Volatility { get; set; }
}
