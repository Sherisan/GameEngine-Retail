using System.Text.Json.Serialization;

namespace Worldiator.Server.Domain.MarginCall;

/// <summary>
/// Margin Call (TradeX) — trading-themed crash game. Bound from the "MarginCall" section; GameId picks the
/// "Games" entry the engine plays. The rest (TradingSettings) is the appsettings copy of the game's Trading
/// block: it seeds dbo.Games on first import and is the fallback while the game has no Trading block.
/// </summary>
public class MarginCallOptions : TradingSettings
{
    /// <summary>The "Games" entry this engine plays (wallet limits, session tokens, SdGaming title).</summary>
    public string GameId { get; set; } = "tradex";
}

/// <summary>
/// Trading settings for Engine = MarginCall (GameDefinition.Trading, managed from the back office).
///
/// House edge comes from two knobs, usable together or alone:
///   CommissionPercent — % of every stake kept at settlement (same edge on every market / multiplier).
///   SpreadBps         — BUY opens at ask and closes at bid, SELL the reverse (edge scales with the multiplier).
/// Prices are a zero-drift random walk (a martingale), so with no commission and no spread RTP is exactly 100%;
/// the defaults (3% + 10 bps) measure 96–97% across all markets and multipliers.
///
/// Commission, spread, multipliers, position cap and round timing apply from the next trade / phase (open
/// positions keep the commission and spread they opened with). TickMs and Markets are read when the engine
/// starts, so changing them needs an API restart.
/// </summary>
public class TradingSettings
{
    public decimal CommissionPercent { get; set; } = 3.0m;
    public double SpreadBps { get; set; } = 10;

    /// <summary>
    /// Risk multipliers players may choose (P/L = stake × multiplier × price move, max loss = stake).
    /// No default here — config binding appends to an array's default values, which would duplicate them.
    /// </summary>
    public int[] Multipliers { get; set; } = [];

    /// <summary>The configured multipliers, or 1/2/5/10 when none are set.</summary>
    [JsonIgnore]
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

    public void Validate(string gameId)
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{gameId}].Trading: {msg}");
        if (CommissionPercent is < 0 or >= 50 || CommissionPercent != Math.Round(CommissionPercent, 2))
            Fail("CommissionPercent must be 0-50 with at most 2 decimals.");
        if (SpreadBps is < 0 or > 1000) Fail("SpreadBps must be 0-1000.");
        if (Multipliers.Any(m => m is < 1 or > 100)) Fail("Multipliers must be 1-100.");
        if (Multipliers.Distinct().Count() != Multipliers.Length) Fail("Multipliers must be unique.");
        if (MaxPositionsPerRound is < 1 or > 100) Fail("MaxPositionsPerRound must be 1-100.");
        if (TickMs is < 20 or > 1000) Fail("TickMs must be 20-1000.");
        if (GrowthRate is <= 0 or > 1) Fail("GrowthRate must be above 0 and at most 1.");
        if (WaitingMs < 1000) Fail("WaitingMs must be >= 1000.");
        if (ClosedMs < 0) Fail("ClosedMs must be >= 0.");
        if (MinOpenMs < 1000) Fail("MinOpenMs must be >= 1000.");
        if (MaxTradingMs < MinOpenMs + ClosedMs) Fail("MaxTradingMs must be at least MinOpenMs + ClosedMs.");
        if (CrashedMs < 200) Fail("CrashedMs must be >= 200.");
        if (SettlingMs < 200) Fail("SettlingMs must be >= 200.");
        if (ResultsMs < 1000) Fail("ResultsMs must be >= 1000.");

        if (Markets.Count == 0) Fail("needs at least one market.");
        foreach (var m in Markets)
        {
            if (string.IsNullOrWhiteSpace(m.Symbol) || m.Symbol.Length > 20) Fail("every market needs a Symbol of 1-20 characters.");
            if (m.BasePrice <= 0) Fail($"{m.Symbol}: BasePrice must be > 0.");
            if (m.Volatility is <= 0 or > 0.1) Fail($"{m.Symbol}: Volatility must be above 0 and at most 0.1 (10% per tick).");
        }
        var duplicate = Markets.GroupBy(m => m.Symbol.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) Fail($"market {duplicate.Key} is listed twice.");
    }

    /// <summary>True when the engine would have to restart to apply the change from this to other.</summary>
    public static bool NeedsRestart(TradingSettings? before, TradingSettings? after, TradingSettings fallback)
    {
        var a = before ?? fallback;
        var b = after ?? fallback;
        return a.TickMs != b.TickMs
            || a.Markets.Count != b.Markets.Count
            || a.Markets.Zip(b.Markets).Any(p => p.First.Symbol != p.Second.Symbol
                                              || p.First.BasePrice != p.Second.BasePrice
                                              || p.First.Volatility != p.Second.Volatility);
    }
}

public class MarginMarketConfig
{
    public string Symbol { get; set; } = string.Empty;
    public double BasePrice { get; set; }
    /// <summary>Per-tick standard deviation as a fraction of price.</summary>
    public double Volatility { get; set; }
}
