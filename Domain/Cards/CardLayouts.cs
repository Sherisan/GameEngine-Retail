namespace Worldiator.Server.Domain;

/// <summary>
/// Visible card layouts for scratch-style Multiplier games (RngSettings.Card). The prize is always drawn
/// first from the game's own RNG; a layout only decides how the card shows that prize, and must pay
/// exactly it. Layout randomness is cosmetic and comes from the OS CSPRNG.
/// </summary>
public static class CardLayouts
{
    public const string MysteryBox = "MysteryBox";
    public const string Bottles = "Bottles";
    public static readonly string[] Known = [MysteryBox, Bottles];

    public static bool IsKnown(string card) => Known.Contains(card, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns why the outcome table can't be shown with this layout, or null if it can.</summary>
    public static string? CheckOutcomes(string card, IEnumerable<decimal> multipliers) =>
        string.Equals(card, MysteryBox, StringComparison.OrdinalIgnoreCase)
            ? MysteryBoxCard.CheckOutcomes(multipliers)
            : null;

    public static object Build(string card, decimal stake, decimal multiplier, RngSettings settings)
    {
        if (string.Equals(card, MysteryBox, StringComparison.OrdinalIgnoreCase))
            return MysteryBoxCard.Build(stake, multiplier, settings.Outcomes.Select(o => o.Multiplier));
        if (string.Equals(card, Bottles, StringComparison.OrdinalIgnoreCase))
            return BottlesCard.Build(stake, multiplier, settings.Outcomes);
        throw new InvalidOperationException($"Unknown card layout \"{card}\".");
    }
}
