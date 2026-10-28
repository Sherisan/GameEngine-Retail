using System.Security.Cryptography;

namespace Worldiator.Server.Domain;

/// <summary>One of the six boxes. Type: cash | silver | gift | golden | empty.</summary>
public record MysteryBoxItem(string Type, decimal? Cash = null, int? Multiplier = null);

/// <summary>Golden Vault bonus: the picked vault always holds Prize; Others are shown in the unpicked vaults.</summary>
public record MysteryVault(decimal Prize, decimal PrizeAmount, decimal[] Others);

/// <summary>
/// The card a player bought. Win = Vault.PrizeAmount when there is a golden box, the card price when there is
/// a gift box (free card), otherwise CashTotal × MultiplierTotal (silver multipliers add together; none = ×1).
/// </summary>
public record MysteryBoxLayout(
    IReadOnlyList<MysteryBoxItem> Boxes,
    decimal CashTotal,
    int MultiplierTotal,
    bool FreeCard,
    MysteryVault? Vault,
    decimal Win);

/// <summary>
/// Mystery Box: 6 wrapped boxes laid out to pay exactly the drawn prize (× card price).
///   0×          — empties, sometimes a lone silver box (nothing to multiply).
///   1×          — either a gift box (free card: the price comes back) or cash adding up to the price.
///   below 20×   — cash boxes in steps of half the price, times 0–2 silver multipliers.
///   20× and up  — a golden box; the Golden Vault pays the prize.
/// Losing cards are random, not built as near misses.
/// </summary>
public static class MysteryBoxCard
{
    public const int BoxCount = 6;
    public const decimal VaultThreshold = 20m;
    private static readonly int[][] MultiplierSets = [[], [2], [3], [5], [10], [2, 3]];

    public static string? CheckOutcomes(IEnumerable<decimal> multipliers)
    {
        foreach (var m in multipliers)
        {
            if (m == 0 || m >= VaultThreshold) continue;
            if (m * 2 != Math.Floor(m * 2))
                return $"MysteryBox can't show a {m}× prize — below {VaultThreshold}× prizes must be multiples of 0.5.";
        }
        return null;
    }

    public static MysteryBoxLayout Build(decimal stake, decimal multiplier, IEnumerable<decimal> tableMultipliers)
    {
        var win = stake * multiplier;
        var boxes = new List<MysteryBoxItem>();
        MysteryVault? vault = null;
        var freeCard = false;

        if (multiplier >= VaultThreshold)
        {
            boxes.Add(new MysteryBoxItem("golden"));
            var others = tableMultipliers.Where(m => m >= VaultThreshold && m != multiplier).Distinct().ToList();
            Shuffle(others);
            vault = new MysteryVault(multiplier, win, others.Take(2).ToArray());
        }
        else if (multiplier == 1 && RandomNumberGenerator.GetInt32(2) == 0)
        {
            boxes.Add(new MysteryBoxItem("gift"));
            freeCard = true;
        }
        else if (multiplier > 0)
        {
            boxes.AddRange(CashBoxes(stake, multiplier));
        }
        else if (RandomNumberGenerator.GetInt32(3) == 0)
        {
            // A lone multiplier on a losing card: nothing to multiply, so it pays nothing.
            boxes.Add(new MysteryBoxItem("silver", Multiplier: Pick([2, 3, 5, 10])));
        }

        while (boxes.Count < BoxCount) boxes.Add(new MysteryBoxItem("empty"));
        Shuffle(boxes);

        var cash  = boxes.Where(b => b.Type == "cash").Sum(b => b.Cash!.Value);
        var mult  = boxes.Where(b => b.Type == "silver").Sum(b => b.Multiplier!.Value);
        var pays  = vault?.PrizeAmount ?? (freeCard ? stake : cash * Math.Max(1, mult));
        if (pays != win)
            throw new InvalidOperationException($"MysteryBox layout pays {pays} but the drawn prize is {win} ({multiplier}× {stake}).");

        return new MysteryBoxLayout(boxes, cash, mult, freeCard, vault, win);
    }

    /// <summary>Cash boxes (multiples of half the price) and silver boxes whose product is exactly the prize.</summary>
    private static IEnumerable<MysteryBoxItem> CashBoxes(decimal stake, decimal multiplier)
    {
        var unit  = stake / 2;
        var units = (int)(multiplier * 2); // prize in half-prices
        var options = MultiplierSets.Where(set => units % Math.Max(1, set.Sum()) == 0).ToList();
        var silvers = options[RandomNumberGenerator.GetInt32(options.Count)];

        var cashUnits = units / Math.Max(1, silvers.Sum());
        var parts = Split(cashUnits, 1 + RandomNumberGenerator.GetInt32(Math.Min(3, cashUnits)));
        foreach (var p in parts) yield return new MysteryBoxItem("cash", Cash: p * unit);
        foreach (var s in silvers) yield return new MysteryBoxItem("silver", Multiplier: s);
    }

    /// <summary>Splits total into count positive parts at random.</summary>
    private static List<int> Split(int total, int count)
    {
        var cuts = new SortedSet<int>();
        while (cuts.Count < count - 1) cuts.Add(1 + RandomNumberGenerator.GetInt32(total - 1));
        var parts = new List<int>();
        var prev = 0;
        foreach (var c in cuts.Append(total)) { parts.Add(c - prev); prev = c; }
        return parts;
    }

    private static int Pick(int[] values) => values[RandomNumberGenerator.GetInt32(values.Length)];

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
