using System.Security.Cryptography;
using Worldiator.Server.Domain.MarginCall;

namespace Worldiator.Server.Application.MarginCall;

/// <summary>
/// One server-side market. Prices decide payouts, so the walk is a martingale: each tick multiplies the
/// price by (1 + zero-mean noise), with an occasional volatility spike. There is deliberately no trend,
/// momentum or mean reversion while positions can be open — any of those lets a player who watches the
/// chart predict direction. Between rounds (nothing open) prices drift back toward BasePrice.
/// Randomness comes from the OS CSPRNG. Not thread-safe; MarginCallEngine calls it under its lock.
/// </summary>
public sealed class MarginMarket
{
    public const int TicksPerCandle = 8;
    private const int MaxCandles = 160;
    private const int HistoryCandles = 120;
    private const double SpikeChance = 0.015;
    private const double SpikeScale = 6;
    /// <summary>Fraction of the (log) distance to BasePrice closed per between-rounds tick — ~40% per break.</summary>
    private const double RecenterRate = 0.005;

    public MarginMarketConfig Config { get; }
    public double Price { get; private set; }

    private readonly List<MarginCandle> _candles = [];
    private long _wTime;
    private double _wOpen, _wHigh, _wLow;
    private int _tickInCandle;

    public MarginMarket(MarginMarketConfig config, long now, int tickMs)
    {
        Config = config;
        Price  = config.BasePrice;
        SeedHistory(now, tickMs);
    }

    /// <param name="recenter">
    /// Pull the price back toward BasePrice. Only pass true between rounds (RESULTS / WAITING), when no position
    /// is open: a predictable pull while positions are open would let players beat the house, but without it the
    /// walk wanders without bound (EUR/USD drifting to 0.4 or 3.0 over a few hours).
    /// </param>
    public void Tick(long now, bool recenter = false)
    {
        var r = Config.Volatility * Gaussian();
        if (NextDouble() < SpikeChance)
            r += Config.Volatility * SpikeScale * Gaussian();
        if (recenter)
            r -= RecenterRate * Math.Log(Price / Config.BasePrice);
        // Floor keeps the price positive; at these volatilities it is never reached in practice.
        Price = Math.Max(Price * (1 + r), Config.BasePrice * 0.01);
        AdvanceCandle(now);
    }

    /// <summary>Completed candles plus the one in progress — what the client chart draws on connect.</summary>
    public List<MarginCandle> Candles() => [.. _candles, new MarginCandle(_wTime, _wOpen, _wHigh, _wLow, Price)];

    private void AdvanceCandle(long now)
    {
        _wHigh = Math.Max(_wHigh, Price);
        _wLow  = Math.Min(_wLow, Price);
        if (++_tickInCandle < TicksPerCandle) return;

        _candles.Add(new MarginCandle(_wTime, _wOpen, _wHigh, _wLow, Price));
        if (_candles.Count > MaxCandles) _candles.RemoveAt(0);
        StartCandle(now);
    }

    private void StartCandle(long time)
    {
        _wTime = time;
        _wOpen = _wHigh = _wLow = Price;
        _tickInCandle = 0;
    }

    private void SeedHistory(long now, int tickMs)
    {
        var candleMs = (long)TicksPerCandle * tickMs;
        var start = now - HistoryCandles * candleMs;
        StartCandle(start);
        for (var i = 0; i < HistoryCandles * TicksPerCandle; i++)
            Tick(start + i / TicksPerCandle * candleMs, recenter: true);
        StartCandle(now);
    }

    /// <summary>Approx. standard normal via the sum of four uniforms (Irwin–Hall), as the original client did.</summary>
    private static double Gaussian()
    {
        double s = 0;
        for (var i = 0; i < 4; i++) s += NextDouble();
        return (s - 2) / 1.1547;
    }

    /// <summary>Uniform [0, 1) with 53 bits from the CSPRNG.</summary>
    public static double NextDouble()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return (BitConverter.ToUInt64(b) >> 11) * (1.0 / (1UL << 53));
    }
}
