namespace Worldiator.Server.Domain;

public enum GameType { Crash, Slot, Prediction, Scratch }

/// <summary>
/// Which engine runs the game. Every engine draws from the game's own RNG instance (IGameRngService keeps
/// one seeded generator per GameId), so no two games share a seed batch or nonce sequence.
///   Instant    — one request per play through api/play (Rng: Multiplier / Pick / Target).
///   LiveCrash  — shared rounds on /livecrashhub: bet, watch the multiplier climb, cash out before the crash (Rng: Target).
///   MarginCall — the trading crash game on /margincallhub (Rng: Target, optional — sets round length only).
///   Custom     — has its own engine and RNG (Feeding Frenzy) or its own backend.
/// </summary>
public enum EngineKind { Instant, LiveCrash, MarginCall, Custom }

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

    public void Validate()
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{GameId}]: {msg}");
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
                break;
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
/// </summary>
public enum RngModel { Multiplier, Pick, Target }

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
        _                   => 1m - (decimal)HouseEdgePercent / 100m,
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
        }
        if (RotateSeedEvery < 1) Fail("RotateSeedEvery must be >= 1.");
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
