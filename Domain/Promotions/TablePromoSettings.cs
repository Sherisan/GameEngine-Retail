namespace Worldiator.Server.Domain.Promotions;

// Settings for the promotions of the instant (card), roulette and blackjack games, and the cross-game ones
// (cashback, streak missions). See Promotion for what each type does.

public class MysteryPrizeSettings
{
    /// <summary>Chance per qualifying paid card, 0-1.</summary>
    public double Chance { get; set; } = 0.02;
    public decimal Amount { get; set; } = 20m;
    public DropPrize Prize { get; set; } = DropPrize.Cash;
    public int FreeBetValidDays { get; set; } = 7;

    public void Validate(decimal budget)
    {
        if (Chance is <= 0 or > 1) throw new InvalidOperationException("Mystery prize chance must be above 0 and at most 1.");
        Promotion.Money(Amount, "Mystery prize");
        Promotion.Days(FreeBetValidDays, "Free cards");
        Promotion.NotAboveBudget(Amount, budget, "Mystery prize");
    }
}

public class BuyXGetFreeSettings
{
    /// <summary>Paid cards per free card. The free card is worth the price of the card that completed the set.</summary>
    public int Buy { get; set; } = 5;
    public int MaxFreePerDay { get; set; } = 5;
    public int FreeBetValidDays { get; set; } = 7;

    public void Validate()
    {
        if (Buy is < 1 or > 100) throw new InvalidOperationException("Buy must be 1-100 cards.");
        if (MaxFreePerDay is < 1 or > 1000) throw new InvalidOperationException("Free cards per day must be 1-1000.");
        Promotion.Days(FreeBetValidDays, "Free cards");
    }
}

public class CollectionSettings
{
    /// <summary>The set to collect (2-12 names, e.g. "Star", "Moon", "Sun").</summary>
    public List<string> Symbols { get; set; } = ["Star", "Moon", "Sun", "Comet"];
    /// <summary>Chance a qualifying paid card carries a symbol (one, at random), 0-1.</summary>
    public double ChancePerCard { get; set; } = 0.5;
    public decimal Reward { get; set; } = 25m;
    public DropPrize Prize { get; set; } = DropPrize.Cash;
    public int FreeBetValidDays { get; set; } = 7;
    /// <summary>Times one player can complete the set. 0 = no limit.</summary>
    public int MaxCompletions { get; set; } = 0;

    public void Validate(decimal budget)
    {
        if (Symbols.Count is < 2 or > 12) throw new InvalidOperationException("A collection needs 2-12 symbols.");
        if (Symbols.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 30)) throw new InvalidOperationException("Every symbol needs a name of 1-30 characters.");
        if (Symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Symbols.Count) throw new InvalidOperationException("Symbols must be different.");
        if (ChancePerCard is <= 0 or > 1) throw new InvalidOperationException("Symbol chance must be above 0 and at most 1.");
        Promotion.Money(Reward, "Collection reward");
        Promotion.Days(FreeBetValidDays, "Free cards");
        if (MaxCompletions is < 0 or > 1000) throw new InvalidOperationException("Completions per player must be 0-1000.");
        Promotion.NotAboveBudget(Reward, budget, "Collection reward");
    }
}

public class CashbackSettings
{
    /// <summary>Share of the net loss (stakes − wins) paid back, 0.01-50 %.</summary>
    public decimal Percent { get; set; } = 10m;
    public CashbackPeriod Period { get; set; } = CashbackPeriod.Daily;
    /// <summary>Only losses on these days count (e.g. a "roulette day"). Empty = every day. UTC.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    /// <summary>Most one player gets back per period.</summary>
    public decimal MaxPerPlayer { get; set; } = 100m;
    /// <summary>Smallest net loss that earns cashback.</summary>
    public decimal MinLoss { get; set; } = 10m;
    /// <summary>Per game, how much of its net loss counts (%, default 100) — e.g. blackjack at 50 for its lower house edge.</summary>
    public Dictionary<string, decimal> GameWeights { get; set; } = [];

    public decimal WeightFor(string gameId) =>
        GameWeights.FirstOrDefault(kv => string.Equals(kv.Key, gameId, StringComparison.OrdinalIgnoreCase)).Value is var w && w > 0 ? w : 100m;

    public void Validate()
    {
        if (Percent is <= 0 or > 50 || Percent != Math.Round(Percent, 2)) throw new InvalidOperationException("Cashback must be 0.01-50 %.");
        Promotion.Money(MaxPerPlayer, "Cashback cap per player");
        if (MinLoss < 0 || MinLoss != Math.Round(MinLoss, 2)) throw new InvalidOperationException("Min loss must be 0 or more with at most 2 decimals.");
        if (GameWeights.Values.Any(w => w is < 0 or > 100)) throw new InvalidOperationException("Game weights must be 0-100 %.");
    }
}

public class StreakSettings
{
    public StreakCounts Counts { get; set; } = StreakCounts.Plays;
    /// <summary>Qualifying plays (or wins) in a row.</summary>
    public int Target { get; set; } = 10;
    /// <summary>A longer break than this between plays starts the streak again.</summary>
    public int MaxGapMinutes { get; set; } = 10;
    public decimal Reward { get; set; } = 10m;
    public DropPrize Prize { get; set; } = DropPrize.Cash;
    public int FreeBetValidDays { get; set; } = 7;
    public int MaxRewardsPerDay { get; set; } = 3;

    public void Validate(decimal budget)
    {
        if (Target is < 2 or > 500) throw new InvalidOperationException("Streak target must be 2-500.");
        if (MaxGapMinutes is < 1 or > 1440) throw new InvalidOperationException("Max gap must be 1-1440 minutes.");
        Promotion.Money(Reward, "Streak reward");
        Promotion.Days(FreeBetValidDays, "Free bets");
        if (MaxRewardsPerDay is < 1 or > 100) throw new InvalidOperationException("Rewards per day must be 1-100.");
        Promotion.NotAboveBudget(Reward, budget, "Streak reward");
    }
}

public class LuckyNumberSettings
{
    /// <summary>The lucky number, 0-36. Null = a new number every day (UTC), the same for everyone.</summary>
    public int? Number { get; set; }
    public decimal Prize { get; set; } = 50m;
    public int MaxPrizesPerPlayerPerDay { get; set; } = 1;

    public void Validate(decimal budget)
    {
        if (Number is < 0 or > 36) throw new InvalidOperationException("The lucky number must be 0-36.");
        Promotion.Money(Prize, "Lucky number prize");
        if (MaxPrizesPerPlayerPerDay is < 1 or > 100) throw new InvalidOperationException("Prizes per player per day must be 1-100.");
        Promotion.NotAboveBudget(Prize, budget, "Lucky number prize");
    }
}

public class BoostedPayoutSettings
{
    /// <summary>Numbers whose straight-up bets pay more (1-10 numbers).</summary>
    public List<int> Numbers { get; set; } = [7, 17, 27];
    /// <summary>Boosted straight-up payout, to 1 (the table pays 35 to 1). The difference is a promo credit.</summary>
    public int Payout { get; set; } = 50;
    /// <summary>Largest straight-up stake the boost applies to, per number per spin.</summary>
    public decimal MaxBoostedStake { get; set; } = 10m;

    public void Validate()
    {
        if (Numbers.Count is < 1 or > 10 || Numbers.Any(n => n is < 0 or > 36) || Numbers.Distinct().Count() != Numbers.Count)
            throw new InvalidOperationException("Pick 1-10 different numbers, 0-36.");
        if (Payout is < 36 or > 500) throw new InvalidOperationException("The boosted payout must be 36-500 to 1.");
        Promotion.Money(MaxBoostedStake, "Max boosted stake");
    }
}

public class HandBonusSettings
{
    public SpecialHand Hand { get; set; } = SpecialHand.SuitedBlackjack;
    public decimal Prize { get; set; } = 25m;
    public int MaxPerPlayerPerDay { get; set; } = 5;

    public void Validate(decimal budget)
    {
        Promotion.Money(Prize, "Hand bonus");
        if (MaxPerPlayerPerDay is < 1 or > 100) throw new InvalidOperationException("Bonuses per player per day must be 1-100.");
        Promotion.NotAboveBudget(Prize, budget, "Hand bonus");
    }
}

public class HandJackpotSettings
{
    public SpecialHand Hand { get; set; } = SpecialHand.SuitedSevens;
    /// <summary>The pool for each day (UTC). A hit wins SharePercent of what's left of today's pool.</summary>
    public decimal Pool { get; set; } = 1000m;
    public decimal SharePercent { get; set; } = 25m;

    public void Validate(decimal budget)
    {
        Promotion.Money(Pool, "Jackpot pool");
        if (SharePercent is <= 0 or > 100) throw new InvalidOperationException("Share must be above 0 and at most 100 %.");
        Promotion.NotAboveBudget(Pool, budget, "Jackpot pool");
    }
}
