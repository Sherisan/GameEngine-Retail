namespace Worldiator.Server.Domain;

/// <summary>
/// One rung of a /gamehub game's evolution ladder: the round multiplier at which the player's
/// fish (Feeding Frenzy) or ship (Comet Rider) becomes this tier. Stored in dbo.GameEvolutionTiers
/// and edited from the Game Back Office.
/// </summary>
/// <param name="Level">0 = ambient "food" tier below the 1.00x launch point, 1 = starting tier, then upwards.</param>
/// <param name="Code">Art key the frontend draws ("shark", "mothership"). Fixed per game.</param>
public record EvolutionTier(int Level, string Code, string Name, decimal Multiplier);

public static class EvolutionTiers
{
    public const decimal StartMultiplier = 1.00m;
    public const decimal MaxMultiplier = 10000m;

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>The ladders the games shipped with — used until the database has been read, or when it has no rows for a game.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<EvolutionTier>> Defaults =
        new Dictionary<string, IReadOnlyList<EvolutionTier>>(StringComparer.OrdinalIgnoreCase)
        {
            ["feeding-frenzy"] =
            [
                new(0, "sardine",    "Sardine",     0.5m),
                new(1, "clownfish",  "Clownfish",   1m),
                new(2, "grouper",    "Grouper",     2m),
                new(3, "tuna",       "Tuna",        5m),
                new(4, "shark",      "Shark",       10m),
                new(5, "whaleshark", "Whale Shark", 25m),
            ],
            ["comet-rider"] =
            [
                new(0, "probe",      "Probe",       0.5m),
                new(1, "scout",      "Scout",       1m),
                new(2, "fighter",    "Fighter",     2m),
                new(3, "cruiser",    "Cruiser",     5m),
                new(4, "destroyer",  "Destroyer",   10m),
                new(5, "mothership", "Mothership",  25m),
            ],
            ["arctic-appetite"] =
            [
                new(0, "krill",      "Krill",       0.5m),
                new(1, "penguin",    "Penguin",     1m),
                new(2, "seal",       "Seal",        2m),
                new(3, "polarbear",  "Polar Bear",  5m),
                new(4, "orca",       "Orca",        10m),
                new(5, "leviathan",  "Ice Leviathan", 25m),
            ],
        };

    /// <summary>
    /// The player's level at a live multiplier: the highest level (≥ 1) whose multiplier has been reached.
    /// Mirrors tierForMultiplier in the frontends' fishTiers.ts / tiers.ts.
    /// </summary>
    public static int LevelAt(IReadOnlyList<EvolutionTier> tiers, double multiplier)
    {
        var level = 1;
        foreach (var t in tiers)
            if (t.Level >= 1 && multiplier >= (double)t.Multiplier && t.Level > level) level = t.Level;
        return level;
    }

    /// <summary>
    /// Returns the problems with a ladder (empty = valid). When <paramref name="current"/> is given, the
    /// codes must match it level for level — the frontends only have art for the codes they ship with.
    /// </summary>
    public static List<string> Validate(IReadOnlyList<EvolutionTier> tiers, IReadOnlyList<EvolutionTier>? current = null)
    {
        var errors = new List<string>();
        if (tiers.Count < 2)
        {
            errors.Add("At least two tiers are needed (level 0 and the level 1 starting tier).");
            return errors;
        }

        var ordered = tiers.OrderBy(t => t.Level).ToList();
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].Level != i)
            {
                errors.Add(Inv($"Levels must run 0 to {ordered.Count - 1} with no gaps or duplicates."));
                return errors;
            }

        foreach (var t in ordered)
        {
            if (string.IsNullOrWhiteSpace(t.Code)) errors.Add(Inv($"Level {t.Level}: Code is required."));
            if (string.IsNullOrWhiteSpace(t.Name)) errors.Add(Inv($"Level {t.Level}: Name is required."));
            else if (t.Name.Length > 64) errors.Add(Inv($"Level {t.Level}: Name must be 64 characters or fewer."));
            if (t.Multiplier <= 0 || t.Multiplier > MaxMultiplier)
                errors.Add(Inv($"Level {t.Level}: Multiplier must be above 0 and at most {MaxMultiplier}."));
            if (t.Multiplier != Math.Round(t.Multiplier, 2))
                errors.Add(Inv($"Level {t.Level}: Multiplier can have at most 2 decimals."));
        }
        if (ordered.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count)
            errors.Add("Codes must be unique.");

        if (ordered[0].Multiplier >= StartMultiplier)
            errors.Add(Inv($"Level 0 is the food tier and must be below {StartMultiplier:0.00}x."));
        if (ordered[1].Multiplier != StartMultiplier)
            errors.Add(Inv($"Level 1 is the starting tier and must be exactly {StartMultiplier:0.00}x."));
        for (var i = 2; i < ordered.Count; i++)
            if (ordered[i].Multiplier <= ordered[i - 1].Multiplier)
                errors.Add(Inv($"Level {i} ({ordered[i].Multiplier}x) must need a higher multiplier than level {i - 1} ({ordered[i - 1].Multiplier}x)."));

        if (current is { Count: > 0 })
        {
            var codes = ordered.Select(t => t.Code);
            var expected = current.OrderBy(t => t.Level).Select(t => t.Code);
            if (!codes.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                errors.Add(Inv($"Codes must stay as they are, in order: {string.Join(", ", expected)}. Adding or renaming a tier needs new art in the game."));
        }
        return errors;
    }
}
