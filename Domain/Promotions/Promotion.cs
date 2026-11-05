using System.Text.Json.Serialization;

namespace Worldiator.Server.Domain.Promotions;

/// <summary>
/// Promo tools, managed from the Game Back Office (api/backoffice/promotions) and run by PromoEngine.
///
/// Any game:
///   FreeBet               — free bets / free cards / free chips granted to players (roulette chips can be limited to bet types).
///   Rain                  — at random times a pool is split among recently active players.
///   MultiplierLeaderboard — players rank by a metric (best multiple, best win, wagered, won, rounds, special hits); prizes per period.
///   MultiplierRace        — the first N players to win at or above a target multiple get a prize.
///   Cashback              — a share of each player's net losses per day / week, optionally only on set days, weighted per game.
///   StreakMission         — N plays (or N wins) in a row earns a reward.
/// Crash games (Feeding Frenzy, Comet Rider, TradeX, live crash):
///   RoundDrop             — each round, a chance that random players in it get cash or a free bet.
///   CommunityJackpot      — a crash point at or above the trigger shares a pool among everyone who bet in that round.
/// Instant / scratch games (Mystery Box, Message in a Bottle):
///   MysteryPrize          — a qualifying card has a chance to reveal a hidden prize.
///   BuyXGetFree           — every X paid cards earns a free card at that card's price.
///   CollectionMission     — cards carry symbols; collecting the whole set unlocks a reward.
/// Roulette:
///   LuckyNumber           — a prize when the lucky number hits and the player has a straight-up bet on it.
///   BoostedPayout         — straight-up wins on chosen numbers pay more; the extra comes from the promotion.
/// Blackjack:
///   HandBonus             — a fixed prize for a special hand (suited blackjack, suited 7s, …).
///   HandJackpot           — a special hand wins a share of the pool.
///
/// Every payout is a tagged cash credit (or a free bet) funded from the promotion's Budget, never from game RTP.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromoType
{
    FreeBet, Rain, RoundDrop, MultiplierLeaderboard, MultiplierRace, CommunityJackpot,
    MysteryPrize, BuyXGetFree, CollectionMission, Cashback, StreakMission, LuckyNumber, BoostedPayout, HandBonus, HandJackpot,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromoState { Off, Scheduled, Live, Ended, BudgetSpent }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FreeBetGrant { Manual, OnFirstVisit }

/// <summary>What a promotion pays: cash, or a free bet of that value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DropPrize { Cash, FreeBet }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeaderboardPeriod { Once, Daily, Weekly }

/// <summary>
/// What a leaderboard ranks by. "Best" metrics keep a player's best single result; "Total" metrics add up.
/// Hits = the game's special result: roulette straight-up wins, blackjacks, a card's top prize.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeaderboardMetric { BestMultiple, BestWin, TotalWagered, TotalWon, RoundsPlayed, Hits }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreakCounts { Plays, Wins }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CashbackPeriod { Daily, Weekly }

/// <summary>Blackjack hands promotions can pay on. Pair / sevens hands look at the first two (or three) cards.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecialHand { Blackjack, SuitedBlackjack, SuitedSevens, ThreeSevens, PerfectPair, FiveCardCharlie }

public class Promotion
{
    public int PromotionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public PromoType Type { get; set; }
    public bool Enabled { get; set; }
    /// <summary>The games it runs in (catalogue GameIds). Their engines must suit the type — see EnginesFor.</summary>
    public List<string> GameIds { get; set; } = [];
    /// <summary>UTC. Null = as soon as it's enabled.</summary>
    public DateTime? StartsAt { get; set; }
    /// <summary>UTC. Null = until disabled (not allowed for a one-off leaderboard).</summary>
    public DateTime? EndsAt { get; set; }
    /// <summary>Most this promotion may pay out in total. Free-bet winnings are always paid, even past it.</summary>
    public decimal Budget { get; set; }
    /// <summary>Smallest real-money stake that counts (per round / card / spin / hand; for roulette lucky numbers, on the number). 0 = any.</summary>
    public decimal MinStake { get; set; }
    /// <summary>Short terms shown to players.</summary>
    public string Terms { get; set; } = string.Empty;

    public FreeBetSettings? FreeBet { get; set; }
    public RainSettings? Rain { get; set; }
    public RoundDropSettings? Drop { get; set; }
    public LeaderboardSettings? Leaderboard { get; set; }
    public RaceSettings? Race { get; set; }
    public JackpotSettings? Jackpot { get; set; }
    public MysteryPrizeSettings? Mystery { get; set; }
    public BuyXGetFreeSettings? BuyX { get; set; }
    public CollectionSettings? Collection { get; set; }
    public CashbackSettings? Cashback { get; set; }
    public StreakSettings? Streak { get; set; }
    public LuckyNumberSettings? Lucky { get; set; }
    public BoostedPayoutSettings? Boost { get; set; }
    public HandBonusSettings? HandBonus { get; set; }
    public HandJackpotSettings? HandJackpot { get; set; }

    private static readonly EngineKind[] Crash = [EngineKind.Custom, EngineKind.LiveCrash, EngineKind.MarginCall];
    private static readonly EngineKind[] All = [.. Crash, EngineKind.Instant, EngineKind.Roulette, EngineKind.Blackjack];

    /// <summary>The engines whose games a promotion of this type can run in.</summary>
    public static IReadOnlyList<EngineKind> EnginesFor(PromoType type) => type switch
    {
        PromoType.RoundDrop or PromoType.CommunityJackpot => Crash,
        PromoType.MysteryPrize or PromoType.BuyXGetFree or PromoType.CollectionMission => [EngineKind.Instant],
        PromoType.LuckyNumber or PromoType.BoostedPayout => [EngineKind.Roulette],
        PromoType.HandBonus or PromoType.HandJackpot => [EngineKind.Blackjack],
        PromoType.StreakMission => [EngineKind.Instant, EngineKind.Roulette, EngineKind.Blackjack],
        _ => All,
    };

    /// <summary>True when this promotion can hand out free bets (so players can play them in its games).</summary>
    public bool GivesFreeBets => Type switch
    {
        PromoType.FreeBet or PromoType.BuyXGetFree => true,
        PromoType.RoundDrop => Drop?.Prize == DropPrize.FreeBet,
        PromoType.MysteryPrize => Mystery?.Prize == DropPrize.FreeBet,
        PromoType.CollectionMission => Collection?.Prize == DropPrize.FreeBet,
        PromoType.StreakMission => Streak?.Prize == DropPrize.FreeBet,
        _ => false,
    };

    public bool AppliesTo(string gameId) => GameIds.Contains(gameId, StringComparer.OrdinalIgnoreCase);

    public bool IsLiveAt(DateTime utcNow) =>
        Enabled && (StartsAt == null || utcNow >= StartsAt) && (EndsAt == null || utcNow < EndsAt);

    public PromoState StateAt(DateTime utcNow, decimal spent)
    {
        if (!Enabled) return PromoState.Off;
        if (EndsAt != null && utcNow >= EndsAt) return PromoState.Ended;
        if (StartsAt != null && utcNow < StartsAt) return PromoState.Scheduled;
        // Free bets already granted keep working; new grants / payouts stop once the budget is used up.
        if (spent >= Budget) return PromoState.BudgetSpent;
        return PromoState.Live;
    }

    public void Validate()
    {
        void Fail(string msg) => throw new InvalidOperationException(msg);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) Fail("Name is required (at most 100 characters).");
        if (Terms.Length > 500) Fail("Terms can be at most 500 characters.");
        if (GameIds.Count == 0) Fail("Pick at least one game.");
        if (Budget <= 0 || Budget != Math.Round(Budget, 2)) Fail("Budget must be a positive amount with at most 2 decimals.");
        if (MinStake < 0 || MinStake != Math.Round(MinStake, 2)) Fail("Min stake must be 0 or more with at most 2 decimals.");
        if (StartsAt != null && EndsAt != null && EndsAt <= StartsAt) Fail("The end must be after the start.");

        var blocks = new (PromoType Type, object? Block)[]
        {
            (PromoType.FreeBet, FreeBet), (PromoType.Rain, Rain), (PromoType.RoundDrop, Drop),
            (PromoType.MultiplierLeaderboard, Leaderboard), (PromoType.MultiplierRace, Race), (PromoType.CommunityJackpot, Jackpot),
            (PromoType.MysteryPrize, Mystery), (PromoType.BuyXGetFree, BuyX), (PromoType.CollectionMission, Collection),
            (PromoType.Cashback, Cashback), (PromoType.StreakMission, Streak), (PromoType.LuckyNumber, Lucky),
            (PromoType.BoostedPayout, Boost), (PromoType.HandBonus, HandBonus), (PromoType.HandJackpot, HandJackpot),
        };
        foreach (var (type, block) in blocks)
        {
            if (type == Type && block == null) Fail($"{Type} needs its settings.");
            if (type != Type && block != null) Fail($"Only the {Type} settings may be set.");
        }

        switch (Type)
        {
            case PromoType.FreeBet: FreeBet!.Validate(Budget); break;
            case PromoType.Rain: Rain!.Validate(Budget); break;
            case PromoType.RoundDrop: Drop!.Validate(Budget); break;
            case PromoType.MultiplierLeaderboard:
                Leaderboard!.Validate(Budget);
                if (Leaderboard.Period == LeaderboardPeriod.Once && EndsAt == null) Fail("A one-off leaderboard needs an end — prizes are paid when it ends.");
                break;
            case PromoType.MultiplierRace: Race!.Validate(Budget); break;
            case PromoType.CommunityJackpot: Jackpot!.Validate(Budget); break;
            case PromoType.MysteryPrize: Mystery!.Validate(Budget); break;
            case PromoType.BuyXGetFree: BuyX!.Validate(); break;
            case PromoType.CollectionMission: Collection!.Validate(Budget); break;
            case PromoType.Cashback: Cashback!.Validate(); break;
            case PromoType.StreakMission: Streak!.Validate(Budget); break;
            case PromoType.LuckyNumber: Lucky!.Validate(Budget); break;
            case PromoType.BoostedPayout: Boost!.Validate(); break;
            case PromoType.HandBonus: HandBonus!.Validate(Budget); break;
            case PromoType.HandJackpot: HandJackpot!.Validate(Budget); break;
        }
    }

    internal static void Money(decimal value, string name)
    {
        if (value <= 0 || value != Math.Round(value, 2))
            throw new InvalidOperationException($"{name} must be a positive amount with at most 2 decimals.");
    }

    internal static void NotAboveBudget(decimal value, decimal budget, string name)
    {
        if (value > budget)
            throw new InvalidOperationException(FormattableString.Invariant($"{name} ({value}) is more than the whole budget ({budget})."));
    }

    internal static void Days(int days, string name)
    {
        if (days is < 1 or > 90) throw new InvalidOperationException($"{name} must be valid for 1-90 days.");
    }
}

public class FreeBetSettings
{
    /// <summary>Value of each free bet. For card games it must be one of the card prices.</summary>
    public decimal Stake { get; set; } = 10m;
    public int BetsPerPlayer { get; set; } = 1;
    public int ValidDays { get; set; } = 7;
    /// <summary>False (usual): a winning free bet pays the winnings above the stake. True: it pays the whole return.</summary>
    public bool StakeReturned { get; set; }
    public FreeBetGrant Grant { get; set; } = FreeBetGrant.Manual;
    /// <summary>Roulette free chips: the bet types the chip may go on (e.g. evenMoney, dozen, column). Empty = any.</summary>
    public List<string> BetTypes { get; set; } = [];

    public void Validate(decimal budget)
    {
        Promotion.Money(Stake, "Free bet stake");
        if (BetsPerPlayer is < 1 or > 100) throw new InvalidOperationException("Free bets per player must be 1-100.");
        Promotion.Days(ValidDays, "Free bets");
        Promotion.NotAboveBudget(Stake, budget, "Free bet stake");
        var unknown = BetTypes.FirstOrDefault(t => !Roulette.RouletteTable.IsBetType(t));
        if (unknown != null)
            throw new InvalidOperationException($"Unknown roulette bet type \"{unknown}\". Known: {string.Join(", ", Roulette.RouletteTable.Payouts.Keys)}.");
    }
}

public class RainSettings
{
    /// <summary>Split equally among the players it lands on.</summary>
    public decimal Pool { get; set; } = 100m;
    public int Recipients { get; set; } = 10;
    public int MinIntervalMinutes { get; set; } = 30;
    public int MaxIntervalMinutes { get; set; } = 60;
    /// <summary>Who counts as active: placed a qualifying bet within this many minutes.</summary>
    public int ActiveWithinMinutes { get; set; } = 10;

    public void Validate(decimal budget)
    {
        Promotion.Money(Pool, "Rain pool");
        if (Recipients is < 1 or > 1000) throw new InvalidOperationException("Rain recipients must be 1-1000.");
        if (MinIntervalMinutes is < 1 or > 1440) throw new InvalidOperationException("Rain interval must be 1-1440 minutes.");
        if (MaxIntervalMinutes < MinIntervalMinutes || MaxIntervalMinutes > 1440)
            throw new InvalidOperationException("Rain max interval must be at least the min interval and at most 1440 minutes.");
        if (ActiveWithinMinutes is < 1 or > 120) throw new InvalidOperationException("Active window must be 1-120 minutes.");
        if (Pool / Recipients < 0.01m) throw new InvalidOperationException("Rain pool is too small to give every recipient at least 0.01.");
        Promotion.NotAboveBudget(Pool, budget, "Rain pool");
    }
}

public class RoundDropSettings
{
    /// <summary>Chance per round, 0-1 (0.05 = one round in 20).</summary>
    public double Chance { get; set; } = 0.05;
    public int Winners { get; set; } = 1;
    /// <summary>Cash amount, or the free bet's stake.</summary>
    public decimal Amount { get; set; } = 10m;
    public DropPrize Prize { get; set; } = DropPrize.Cash;
    /// <summary>When Prize = FreeBet.</summary>
    public int FreeBetValidDays { get; set; } = 7;

    public void Validate(decimal budget)
    {
        if (Chance is <= 0 or > 1) throw new InvalidOperationException("Drop chance must be above 0 and at most 1.");
        if (Winners is < 1 or > 100) throw new InvalidOperationException("Drop winners must be 1-100.");
        Promotion.Money(Amount, "Drop amount");
        Promotion.Days(FreeBetValidDays, "Free bets");
        Promotion.NotAboveBudget(Amount, budget, "Drop amount");
    }
}

public class LeaderboardSettings
{
    /// <summary>Prize by rank: Prizes[0] for 1st, Prizes[1] for 2nd, …</summary>
    public List<decimal> Prizes { get; set; } = [100m, 50m, 25m];
    public LeaderboardPeriod Period { get; set; } = LeaderboardPeriod.Once;
    public LeaderboardMetric Metric { get; set; } = LeaderboardMetric.BestMultiple;

    public void Validate(decimal budget)
    {
        if (Prizes.Count is < 1 or > 100) throw new InvalidOperationException("Leaderboards pay 1-100 places.");
        foreach (var p in Prizes) Promotion.Money(p, "Every leaderboard prize");
        Promotion.NotAboveBudget(Prizes.Sum(), budget, "One period's prizes");
    }
}

public class RaceSettings
{
    public decimal TargetMultiplier { get; set; } = 10m;
    public int Winners { get; set; } = 10;
    public decimal Prize { get; set; } = 50m;

    public void Validate(decimal budget)
    {
        if (TargetMultiplier < 1.01m || TargetMultiplier != Math.Round(TargetMultiplier, 2))
            throw new InvalidOperationException("Race target must be at least 1.01× with at most 2 decimals.");
        if (Winners is < 1 or > 1000) throw new InvalidOperationException("Race winners must be 1-1000.");
        Promotion.Money(Prize, "Race prize");
        Promotion.NotAboveBudget(Prize, budget, "Race prize");
    }
}

public class JackpotSettings
{
    /// <summary>A round whose crash point reaches this triggers the jackpot.</summary>
    public decimal TriggerMultiplier { get; set; } = 50m;
    public decimal Pool { get; set; } = 500m;
    /// <summary>False: equal shares. True: shares in proportion to each player's stake in the round.</summary>
    public bool SplitByStake { get; set; }
    public int MaxHitsPerDay { get; set; } = 1;

    public void Validate(decimal budget)
    {
        if (TriggerMultiplier < 2m || TriggerMultiplier != Math.Round(TriggerMultiplier, 2))
            throw new InvalidOperationException("Jackpot trigger must be at least 2× with at most 2 decimals.");
        Promotion.Money(Pool, "Jackpot pool");
        if (MaxHitsPerDay is < 1 or > 100) throw new InvalidOperationException("Jackpot hits per day must be 1-100.");
        Promotion.NotAboveBudget(Pool, budget, "Jackpot pool");
    }
}
