using System.Text.Json.Serialization;
using Worldiator.Server.Domain.MarginCall;
using Worldiator.Server.Domain.Blackjack;
using Worldiator.Server.Domain.Defuse;
using Worldiator.Server.Domain.Vault;

namespace Worldiator.Server.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameType { Crash, Slot, Prediction, Scratch, Table }

/// <summary>
/// Which engine runs the game. Every engine draws from the game's own RNG instance (IGameRngService keeps
/// one seeded generator per GameId), so no two games share a seed batch or nonce sequence.
///   Instant    — one request per play through api/play (Rng: Multiplier / Pick / Target).
///   LiveCrash  — shared rounds on /livecrashhub: bet, watch the multiplier climb, cash out before the crash (Rng: Target).
///   MarginCall — the trading crash game on /margincallhub (Rng: Target, optional — sets round length only).
///   Roulette   — on-demand European roulette through api/roulette: many bets per spin, one draw (Rng: Wheel).
///   Blackjack  — on-demand blackjack through api/blackjack, one player against the dealer. Each hand is dealt from a
///                freshly shuffled shoe with its own provably-fair seed (no Rng block).
///   Defuse     — DEFUSE through api/defuse: cut wires one at a time, cash out before the detonator. Fixed 97% ladder
///                (DefuseRules) and its own provably-fair seed per round (no Rng block).
///   Vault      — VAULT CRACKER through api/vault: crack a 5-digit combination, 1-in-4 alarm per digit, cash out any time.
///                Fixed 97% ladder (VaultRules) and its own provably-fair seed per round (no Rng block).
///   Custom     — has its own engine and RNG (Feeding Frenzy) or its own backend.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EngineKind { Instant, LiveCrash, MarginCall, Roulette, Custom, Blackjack, Defuse, Vault }

/// <summary>
/// A game that can be launched through this API and use its shared wallet endpoints.
/// Bound from the "Games" array in appsettings.json.
/// </summary>
public class GameDefinition
{
    /// <summary>Stable id carried in the player's session token (e.g. "feeding-frenzy").</summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>
    /// This game's Game.ProviderGameID in the platform catalogue (BetAvenue, SdGaming provider). It's a number there,
    /// so Games-API launches the game with it and this API sends it back on every wallet call so Games-API books the
    /// bet under the right title. Null = Feeding Frenzy, which Games-API identifies by default.
    /// </summary>
    public long? ProviderGameId { get; set; }

    public string Name { get; set; } = string.Empty;
    public GameType Type { get; set; } = GameType.Crash;
    public EngineKind Engine { get; set; } = EngineKind.Instant;

    /// <summary>Frontend URL the player is sent to; the session token is appended as ?token=.</summary>
    public string LaunchUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret the game's own backend sends as X-Game-Key to debit/credit/rollback.
    /// Leave empty for games that don't use the wallet endpoints (e.g. Feeding Frenzy, which settles in-process).
    /// </summary>
    public string ServerKey { get; set; } = string.Empty;

    public decimal MinBet { get; set; } = 1.00m;
    public decimal MaxBet { get; set; } = 10000.00m;

    /// <summary>Cap on the total credited to one player for one round.</summary>
    public decimal MaxWin { get; set; } = 500000.00m;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How this API's RNG plays the game via POST api/play. Null = the game doesn't play through
    /// this API (e.g. Feeding Frenzy has its own engine, or the game has its own backend).
    /// </summary>
    public RngSettings? Rng { get; set; }

    /// <summary>Round timing for Engine = LiveCrash. Defaults apply when omitted.</summary>
    public LiveCrashSettings? Live { get; set; }

    /// <summary>Table limits for Engine = Roulette. Defaults apply when omitted.</summary>
    public RouletteSettings? Roulette { get; set; }

    /// <summary>Markets, house edge and round timing for Engine = MarginCall. Null = the appsettings "MarginCall" section.</summary>
    public TradingSettings? Trading { get; set; }

    /// <summary>Table rules for Engine = Blackjack. Defaults apply when omitted.</summary>
    public BlackjackSettings? Blackjack { get; set; }

    public void Validate()
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{GameId}]: {msg}");
        if (string.IsNullOrWhiteSpace(Name)) Fail("Name is required.");
        if (MinBet <= 0 || MaxBet < MinBet || MaxWin <= 0) Fail("Limits need MinBet > 0, MaxBet >= MinBet and MaxWin > 0.");
        if (MinBet != Math.Round(MinBet, 2) || MaxBet != Math.Round(MaxBet, 2) || MaxWin != Math.Round(MaxWin, 2))
            Fail("MinBet, MaxBet and MaxWin can have at most 2 decimals.");
        if (LaunchUrl.Length > 0 && !(Uri.TryCreate(LaunchUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
            Fail("LaunchUrl must be an absolute http(s) URL, or empty.");
        Rng?.Validate(GameId);
        switch (Engine)
        {
            case EngineKind.LiveCrash:
                if (Rng?.Model != RngModel.Target)
                    Fail("Engine LiveCrash needs an Rng block with Model Target (it supplies the crash point).");
                (Live ??= new LiveCrashSettings()).Validate(GameId);
                break;
            case EngineKind.Instant when Rng?.Card != null && Rng.Stakes.Length > 0:
                var top = Rng.Stakes.Max() * Rng.Outcomes.Max(o => o.Multiplier);
                if (top > MaxWin) Fail($"MaxWin {MaxWin} is below the top prize {top} — the card couldn't pay what it shows.");
                break;
            case EngineKind.MarginCall:
                if (Rng != null && Rng.Model != RngModel.Target)
                    Fail("Engine MarginCall takes an Rng block with Model Target, or none.");
                Trading?.Validate(GameId);
                break;
            case EngineKind.Roulette:
                if (Rng?.Model != RngModel.Wheel)
                    Fail("Engine Roulette needs an Rng block with Model Wheel (it draws the winning pocket).");
                (Roulette ??= new RouletteSettings()).Validate(GameId, MaxBet);
                break;
            case EngineKind.Blackjack:
                if (Rng != null) Fail("Engine Blackjack shuffles its own provably-fair shoe — remove the Rng block.");
                (Blackjack ??= new BlackjackSettings()).Validate(GameId);
                break;
            case EngineKind.Defuse:
                if (Rng != null) Fail("Engine Defuse draws its own provably-fair detonator — remove the Rng block.");
                if (MaxWin < MaxBet * DefuseRules.TopMultiplier)
                    Fail($"MaxWin {MaxWin} is below MaxBet × {DefuseRules.TopMultiplier} — a full defuse couldn't be paid.");
                break;
            case EngineKind.Vault:
                if (Rng != null) Fail("Engine Vault draws its own provably-fair alarms — remove the Rng block.");
                if (MaxWin < MaxBet * VaultRules.TopMultiplier)
                    Fail($"MaxWin {MaxWin} is below MaxBet × {VaultRules.TopMultiplier} — opening the vault couldn't be paid.");
                break;
        }
        if (Rng?.Model == RngModel.Wheel && Engine != EngineKind.Roulette)
            Fail("Rng Model Wheel is only used by Engine Roulette.");
        if (Trading != null && Engine != EngineKind.MarginCall)
            Fail("Trading is only used by Engine MarginCall.");
        if (Blackjack != null && Engine != EngineKind.Blackjack)
            Fail("Blackjack is only used by Engine Blackjack.");
    }
}

/// <summary>
/// Roulette table limits. The total stake per spin is bounded by the game's MinBet / MaxBet;
/// Limits caps the amount on one position, by bet type (see Domain/Roulette/RouletteTable).
/// </summary>
public class RouletteSettings
{
    /// <summary>Most separate positions in one spin (call bets alone use up to 9).</summary>
    public int MaxBetsPerSpin { get; set; } = 150;

    /// <summary>Max per position by bet type (straight, split, street, trio, corner, four, line, column, dozen, evenMoney). Missing = the game's MaxBet.</summary>
    public Dictionary<string, decimal> Limits { get; set; } = [];

    public decimal LimitFor(string betType, decimal maxBet)
    {
        foreach (var (type, limit) in Limits)
            if (string.Equals(type, betType, StringComparison.OrdinalIgnoreCase)) return limit;
        return maxBet;
    }

    public void Validate(string gameId, decimal maxBet)
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{gameId}].Roulette: {msg}");
        if (MaxBetsPerSpin is < 1 or > 500) Fail("MaxBetsPerSpin must be 1-500.");
        foreach (var (type, limit) in Limits)
        {
            if (!Roulette.RouletteTable.IsBetType(type))
                Fail($"unknown bet type \"{type}\" in Limits. Known: {string.Join(", ", Roulette.RouletteTable.Payouts.Keys)}.");
            if (limit <= 0 || limit != Math.Round(limit, 2)) Fail($"Limits.{type} must be a positive amount with at most 2 decimals.");
            if (limit > maxBet) Fail($"Limits.{type} ({limit}) is above the game's MaxBet ({maxBet}).");
        }
    }
}

/// <summary>Live crash round timing. Multiplier at t seconds into the flight = e^(GrowthRate × t).</summary>
public class LiveCrashSettings
{
    /// <summary>Betting window before each flight.</summary>
    public int BettingMs { get; set; } = 6000;
    /// <summary>How long the crash result stays up before the next betting window.</summary>
    public int CrashedMs { get; set; } = 3000;
    public int TickMs { get; set; } = 100;
    /// <summary>Per second. 0.06 → 2× at ~11.6s, 10× at ~38s, 100× at ~77s.</summary>
    public double GrowthRate { get; set; } = 0.06;
    /// <summary>Simultaneous bets per player per round (bet slots).</summary>
    public int MaxBetsPerPlayer { get; set; } = 2;
    public decimal MinAutoCashOut { get; set; } = 1.01m;
    /// <summary>Past crash points kept for the ticker.</summary>
    public int HistorySize { get; set; } = 30;

    public void Validate(string gameId)
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{gameId}].Live: {msg}");
        if (BettingMs < 1000) Fail("BettingMs must be >= 1000.");
        if (CrashedMs < 500) Fail("CrashedMs must be >= 500.");
        if (TickMs is < 20 or > 1000) Fail("TickMs must be 20-1000.");
        if (GrowthRate <= 0) Fail("GrowthRate must be > 0.");
        if (MaxBetsPerPlayer < 1) Fail("MaxBetsPerPlayer must be >= 1.");
        if (MinAutoCashOut < 1.01m) Fail("MinAutoCashOut must be >= 1.01.");
    }
}

/// <summary>
/// Generic RNG play models. Anything these can't express gets its own engine later.
///   Multiplier — draw a payout multiplier from a weighted table; the frontend renders a matching screen (slots, wheels, scratch).
///   Pick       — the player picks an option, the RNG draws one; a match pays that option's Payout (predictions).
///   Target     — the player picks a target multiplier, the RNG draws a crash point; crash ≥ target pays target (instant crash / limbo).
///   Wheel      — the RNG draws one of Pockets equally likely pockets (draw = pocket number); used by Engine Roulette.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RngModel { Multiplier, Pick, Target, Wheel }

public class RngSettings
{
    public RngModel Model { get; set; }

    /// <summary>Multiplier model: the payout table. Multiplier 0 = loss.</summary>
    public List<RngOutcome> Outcomes { get; set; } = [];

    /// <summary>Pick model: the options the player can choose from and the RNG draws from.</summary>
    public List<RngOption> Options { get; set; } = [];

    /// <summary>Target model: house edge and the highest crash point.</summary>
    public double HouseEdgePercent { get; set; } = 4.0;
    public decimal MaxMultiplier { get; set; } = 10000m;

    /// <summary>Wheel model: number of pockets. 37 = European single-zero roulette (0-36).</summary>
    public int Pockets { get; set; } = 37;

    /// <summary>Rotate (and reveal) the server seed after this many draws so past results can be verified.</summary>
    public int RotateSeedEvery { get; set; } = 1000;

    /// <summary>
    /// Multiplier model only: builds the visible card for each play from the drawn prize (e.g. "MysteryBox").
    /// The prize is drawn first; the layout only decorates it and always pays exactly that prize.
    /// </summary>
    public string? Card { get; set; }

    /// <summary>Optional fixed card prices (e.g. 2, 5, 10, 20, 50). Empty = any stake within MinBet–MaxBet.</summary>
    public decimal[] Stakes { get; set; } = [];

    /// <summary>Theoretical return to player (0-1). For Pick it's the best option's.</summary>
    public decimal Rtp => Model switch
    {
        RngModel.Multiplier => Outcomes.Sum(o => (decimal)o.Weight * o.Multiplier) / Math.Max(1, Outcomes.Sum(o => (decimal)o.Weight)),
        RngModel.Pick       => Options.Count == 0 ? 0 : Options.Max(o => o.Weight * o.Payout) / Math.Max(1, Options.Sum(o => (decimal)o.Weight)),
        // Every roulette bet pays (36 / numbers covered) - 1 to 1, so each returns 36 / Pockets.
        RngModel.Wheel      => 36m / Math.Max(1, Pockets),
        _                  => 1m - (decimal)HouseEdgePercent / 100m,
    };

    public void Validate(string gameId)
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{gameId}].Rng: {msg}");
        switch (Model)
        {
            case RngModel.Multiplier:
                if (Outcomes.Count == 0) Fail("Multiplier model needs Outcomes.");
                if (Outcomes.Any(o => o.Weight == 0 || o.Multiplier < 0)) Fail("every outcome needs Weight > 0 and Multiplier >= 0.");
                if (Outcomes.Select(o => o.Multiplier).Distinct().Count() != Outcomes.Count) Fail("outcome multipliers must be unique (merge their weights).");
                break;
            case RngModel.Pick:
                if (Options.Count < 2) Fail("Pick model needs at least 2 Options.");
                if (Options.Any(o => string.IsNullOrWhiteSpace(o.Key) || o.Weight == 0 || o.Payout < 1))
                    Fail("every option needs a Key, Weight > 0 and Payout >= 1.");
                if (Options.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Options.Count) Fail("option keys must be unique.");
                break;
            case RngModel.Target:
                if (HouseEdgePercent is < 0 or >= 100) Fail("HouseEdgePercent must be 0-100.");
                if (MaxMultiplier < 1.01m) Fail("MaxMultiplier must be >= 1.01.");
                break;
            case RngModel.Wheel:
                if (Pockets != 37) Fail("Wheel model supports the European single-zero wheel only (Pockets 37).");
                break;
        }
        if (RotateSeedEvery < 1) Fail("RotateSeedEvery must be >= 1.");
        // A table that returns more than it takes in loses money on every play — almost always a typo in a weight.
        if (Rtp > 1m) Fail(FormattableString.Invariant($"RTP is {Rtp * 100:0.##}% — above 100%, the game would pay out more than it takes in."));
        if (Card != null)
        {
            if (Model != RngModel.Multiplier) Fail("Card layouts need the Multiplier model.");
            if (!CardLayouts.IsKnown(Card)) Fail($"unknown Card \"{Card}\". Known: {string.Join(", ", CardLayouts.Known)}.");
            var problem = CardLayouts.CheckOutcomes(Card, Outcomes.Select(o => o.Multiplier));
            if (problem != null) Fail(problem);
        }
        if (Stakes.Any(s => s <= 0 || s != Math.Round(s, 2))) Fail("Stakes must be positive amounts with at most 2 decimals.");
    }
}

public class RngOutcome
{
    public decimal Multiplier { get; set; }
    public ulong Weight { get; set; }
}

public class RngOption
{
    public string Key { get; set; } = string.Empty;
    public ulong Weight { get; set; }
    /// <summary>Total return on a winning pick, stake included (2 = double the stake).</summary>
    public decimal Payout { get; set; }
}
