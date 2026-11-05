using System.Text.Json;
using System.Text.Json.Serialization;
using Worldiator.Server.Domain.MarginCall;

namespace Worldiator.Server.Domain;

/// <summary>Rules and helpers for the whole game catalogue (dbo.Games, seeded from appsettings "Games").</summary>
public static class GameCatalogue
{
    /// <summary>How the Rng / Live / Roulette / Trading blocks are stored as JSON — same shape as appsettings.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Deep copy, so an edit never touches a definition that's already being served.</summary>
    public static GameDefinition Clone(GameDefinition game) =>
        JsonSerializer.Deserialize<GameDefinition>(JsonSerializer.Serialize(game, Json), Json)!;

    /// <summary>
    /// The games listed in appsettings "Games" — the seed for storage and the fallback without it. The Margin Call
    /// game gets its Trading block from the "MarginCall" section unless its entry has one of its own.
    /// </summary>
    public static List<GameDefinition> FromConfiguration(IConfiguration config)
    {
        var games = config.GetSection("Games").Get<List<GameDefinition>>() ?? [];
        var section = config.GetSection("MarginCall");
        var marginCall = section.Get<MarginCallOptions>();
        if (marginCall != null && marginCall.Markets.Count > 0)
        {
            var game = games.FirstOrDefault(g => g.Engine == EngineKind.MarginCall
                && string.Equals(g.GameId, marginCall.GameId, StringComparison.OrdinalIgnoreCase));
            if (game is { Trading: null })
                game.Trading = section.Get<TradingSettings>();
        }
        return games;
    }

    /// <summary>JSON for the audit trail — everything except the ServerKey secret (shown only as set / not set).</summary>
    public static string AuditJson(GameDefinition game)
    {
        var copy = Clone(game);
        copy.ServerKey = string.IsNullOrEmpty(game.ServerKey) ? "" : "(set)";
        return JsonSerializer.Serialize(copy, Json);
    }

    /// <summary>Validates every game and the rules across games. Throws InvalidOperationException on the first problem.</summary>
    public static void Validate(IReadOnlyList<GameDefinition> games)
    {
        if (games.Any(g => string.IsNullOrWhiteSpace(g.GameId)))
            throw new InvalidOperationException("Games: every entry needs a GameId.");
        var duplicate = games.GroupBy(g => g.GameId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Games: duplicate GameId \"{duplicate.Key}\".");

        foreach (var game in games)
            game.Validate();

        var sharedId = games.Where(g => g.ProviderGameId != null).GroupBy(g => g.ProviderGameId).FirstOrDefault(g => g.Count() > 1);
        if (sharedId != null)
            throw new InvalidOperationException($"Games: ProviderGameId {sharedId.Key} is used by more than one game.");
        var sharedKey = games.Where(g => !string.IsNullOrEmpty(g.ServerKey)).GroupBy(g => g.ServerKey).FirstOrDefault(g => g.Count() > 1);
        if (sharedKey != null)
            throw new InvalidOperationException($"Games: {string.Join(", ", sharedKey.Select(g => g.GameId))} share a ServerKey.");
    }

    /// <summary>Validation message for a catalogue, or null when it's valid.</summary>
    public static string? Problem(IReadOnlyList<GameDefinition> games)
    {
        try { Validate(games); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }
}
