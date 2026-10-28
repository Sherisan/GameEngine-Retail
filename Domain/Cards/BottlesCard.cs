using System.Security.Cryptography;

namespace Worldiator.Server.Domain;

/// <summary>
/// The bottles washed ashore for one play. The bottle the player picks always holds Prize; Others are what
/// the four bottles they didn't pick held, revealed afterwards.
/// </summary>
public record BottlesLayout(decimal Prize, decimal PrizeAmount, decimal[] Others, decimal Win);

/// <summary>
/// Message in a Bottle: five bottles, the player picks one. The prize is drawn first from the game's own RNG
/// and is what the picked bottle holds — which bottle is picked never changes the result.
///
/// The other four bottles are independent draws from the same weighted prize table (cosmetic randomness from
/// the OS CSPRNG), so "what you missed" shows the real odds. They are never chosen to look like near misses.
/// </summary>
public static class BottlesCard
{
    public const int BottleCount = 5;

    public static BottlesLayout Build(decimal stake, decimal multiplier, IReadOnlyList<RngOutcome> outcomes)
    {
        var total = outcomes.Sum(o => (decimal)o.Weight);
        var others = new decimal[BottleCount - 1];
        for (var i = 0; i < others.Length; i++) others[i] = DrawWeighted(outcomes, total);

        var win = stake * multiplier;
        return new BottlesLayout(multiplier, win, others, win);
    }

    private static decimal DrawWeighted(IReadOnlyList<RngOutcome> outcomes, decimal total)
    {
        // Uniform in [0, total) from two 31-bit CSPRNG draws — fine-grained enough for any realistic table.
        var r = ((decimal)RandomNumberGenerator.GetInt32(int.MaxValue) * int.MaxValue
                 + RandomNumberGenerator.GetInt32(int.MaxValue)) / ((decimal)int.MaxValue * int.MaxValue) * total;
        foreach (var o in outcomes)
        {
            if (r < o.Weight) return o.Multiplier;
            r -= o.Weight;
        }
        return outcomes[^1].Multiplier;
    }
}
