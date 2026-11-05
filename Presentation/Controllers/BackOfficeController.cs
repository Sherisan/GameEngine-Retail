using Worldiator.Server.Application;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.MarginCall;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Game Back Office: view and edit every game's configuration — the catalogue entry (limits, enabled, launch URL,
/// RNG / payout table, live crash timing, roulette limits, TradeX trading settings) and, for the /gamehub games, the evolution ladder.
/// Needs a signed-in back-office account (api/backoffice/auth/login): Viewers read, Editors and Admins change.
/// Changes are recorded under the account's username and are live within BackOffice:ConfigRefreshSeconds on every instance.
/// </summary>
[ApiController]
[Route("api/backoffice")]
[Authorize(Policy = BackOfficeAuthService.ViewerPolicy)]
public partial class BackOfficeController(IGameConfigStore store, IOptions<MarginCallOptions> marginCall, ILogger<BackOfficeController> logger)
    : ControllerBase
{
    /// <summary>Who to record in the change history — the signed-in account.</summary>
    private string ChangedBy => User.Identity!.Name!;

    private GameDefinition? FindGame(string gameId) =>
        store.Games.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    // ── Games ───────────────────────────────────────────────────────────────

    /// <summary>Every game in the catalogue, enabled or not.</summary>
    [HttpGet("games")]
    public IActionResult ListGames()
    {
        return Ok(store.Games.Select(g => new
        {
            g.GameId,
            g.ProviderGameId,
            g.Name,
            g.Type,
            g.Engine,
            g.Enabled,
            g.MinBet,
            g.MaxBet,
            g.MaxWin,
            Model = g.Rng?.Model,
            Rtp = g.Rng == null ? (decimal?)null : Math.Round(g.Rng.Rtp, 4),
            HasEvolution = store.GetEvolutionTiers(g.GameId).Count > 0,
        }));
    }

    /// <summary>One game's full configuration — the shape PUT takes back. The ServerKey itself is never returned.</summary>
    [HttpGet("games/{gameId}")]
    public IActionResult GetGame(string gameId)
    {
        var game = FindGame(gameId);
        return game == null ? NotFound(new { error = $"Unknown game \"{gameId}\"." }) : Ok(Detail(game));
    }

    /// <summary>
    /// Replaces a game's configuration: send every field, as GET returned it. Engine and GameId can't change.
    /// ServerKey: omit or null to keep the current one, "" to remove it, or a new value to rotate it.
    /// </summary>
    [HttpPut("games/{gameId}")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> UpdateGame(string gameId, [FromBody] GameBody body)
    {
        var current = FindGame(gameId);
        if (current == null) return NotFound(new { error = $"Unknown game \"{gameId}\"." });
        if (body.Engine != null && body.Engine != current.Engine)
            return BadRequest(new { errors = new[] { $"Engine can't be changed (it is {current.Engine}). Create a new game instead." } });

        var game = Apply(body, current.GameId, current.Engine, current.ServerKey);
        return await SaveAsync(current, game, ChangedBy);
    }

    /// <summary>Adds a game to the catalogue. GameId: lower-case letters, digits and hyphens.</summary>
    [HttpPost("games")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> CreateGame([FromBody] GameBody body)
    {
        if (body.GameId == null || !GameIdPattern().IsMatch(body.GameId))
            return BadRequest(new { errors = new[] { "GameId is required: 2-64 lower-case letters, digits and hyphens (e.g. \"lucky-wheel\")." } });
        if (FindGame(body.GameId) != null) return Conflict(new { errors = new[] { $"Game \"{body.GameId}\" already exists." } });
        if (body.Engine == null) return BadRequest(new { errors = new[] { "Engine is required." } });

        var game = Apply(body, body.GameId, body.Engine.Value, currentServerKey: "");
        return await SaveAsync(null, game, ChangedBy);
    }

    private static GameDefinition Apply(GameBody body, string gameId, EngineKind engine, string currentServerKey) =>
        // Round-trip through JSON so the saved definition shares nothing with the request.
        GameCatalogue.Clone(new GameDefinition
        {
            GameId = gameId,
            Engine = engine,
            ProviderGameId = body.ProviderGameId,
            Name = body.Name?.Trim() ?? "",
            Type = body.Type ?? GameType.Crash,
            LaunchUrl = body.LaunchUrl?.Trim() ?? "",
            ServerKey = body.ServerKey ?? currentServerKey,
            MinBet = body.MinBet,
            MaxBet = body.MaxBet,
            MaxWin = body.MaxWin,
            Enabled = body.Enabled,
            Rng = body.Rng,
            Live = body.Live,
            Roulette = body.Roulette,
            Trading = body.Trading,
            Blackjack = body.Blackjack,
        });

    private async Task<IActionResult> SaveAsync(GameDefinition? current, GameDefinition game, string changedBy)
    {
        // Validate the whole catalogue with this game in it (unique ProviderGameId / ServerKey across games).
        var games = store.Games.Where(g => g != current).Append(game).ToList();
        if (GameCatalogue.Problem(games) is { } problem) return BadRequest(new { errors = new[] { problem } });

        await store.SaveGameAsync(game, changedBy);
        logger.LogInformation("Game {GameId} {Action} by {ChangedBy}", game.GameId, current == null ? "created" : "updated", changedBy);

        return Ok(new { game = Detail(game), restartRequired = RestartRequired(current, game) });
    }

    /// <summary>Whether the change only takes effect after the API restarts.</summary>
    private bool RestartRequired(GameDefinition? current, GameDefinition game) => game.Engine switch
    {
        // Live crash engines are built once at startup (LiveCrashRegistry); their round timing is read then too.
        EngineKind.LiveCrash => current == null
            || current.Enabled != game.Enabled
            || JsonSerializer.Serialize(current.Live, GameCatalogue.Json) != JsonSerializer.Serialize(game.Live, GameCatalogue.Json),
        // The Margin Call engine builds its markets and tick timer once at startup; everything else applies live.
        EngineKind.MarginCall => string.Equals(game.GameId, marginCall.Value.GameId, StringComparison.OrdinalIgnoreCase)
            && TradingSettings.NeedsRestart(current?.Trading, game.Trading, marginCall.Value),
        _ => false,
    };

    private object Detail(GameDefinition g) => new
    {
        g.GameId,
        g.ProviderGameId,
        g.Name,
        g.Type,
        g.Engine,
        g.LaunchUrl,
        HasServerKey = !string.IsNullOrEmpty(g.ServerKey),
        g.MinBet,
        g.MaxBet,
        g.MaxWin,
        g.Enabled,
        g.Rng,
        g.Live,
        g.Roulette,
        g.Trading,
        g.Blackjack,
        HasEvolution = store.GetEvolutionTiers(g.GameId).Count > 0,
    };

    // ── Evolution ladder (/gamehub games) ──────────────────────────────────

    [HttpGet("games/{gameId}/evolution")]
    public IActionResult GetEvolution(string gameId)
    {
        var tiers = store.GetEvolutionTiers(gameId);
        if (tiers.Count == 0) return NotFound(new { error = $"Game \"{gameId}\" has no evolution ladder." });
        return Ok(new { gameId, tiers });
    }

    /// <summary>
    /// Replaces a game's evolution ladder. Send every tier; codes must stay as they are (the game has art
    /// for them) — multipliers and names can change. The games pick it up the next time a player loads them.
    /// </summary>
    [HttpPut("games/{gameId}/evolution")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> UpdateEvolution(string gameId, [FromBody] UpdateEvolutionBody body)
    {
        if (FindGame(gameId) == null) return NotFound(new { error = $"Unknown game \"{gameId}\"." });

        var current = store.GetEvolutionTiers(gameId);
        var tiers = (body.Tiers ?? []).Select(t => new EvolutionTier(t.Level, t.Code?.Trim() ?? "", t.Name?.Trim() ?? "", t.Multiplier)).ToList();
        var errors = EvolutionTiers.Validate(tiers, current);
        if (errors.Count > 0) return BadRequest(new { errors });

        // Keep the stored code casing so the frontends' art lookup still matches.
        if (current.Count > 0)
            tiers = tiers.Select(t => t with { Code = current.First(c => c.Level == t.Level).Code }).ToList();

        await store.SaveEvolutionTiersAsync(gameId, tiers, ChangedBy);
        logger.LogInformation("Evolution ladder for {GameId} changed by {ChangedBy}: {Multipliers}",
            gameId, ChangedBy, string.Join(", ", tiers.OrderBy(t => t.Level).Select(t => $"{t.Code}={t.Multiplier}")));
        return Ok(new { gameId, tiers = store.GetEvolutionTiers(gameId) });
    }

    // ── History ─────────────────────────────────────────────────────────────

    /// <summary>Changes to a game, newest first. section = Game | Evolution, or omit for both.</summary>
    [HttpGet("games/{gameId}/history")]
    public async Task<IActionResult> GetHistory(string gameId, [FromQuery] string? section = null, [FromQuery] int count = 50)
    {
        if (section != null && section != GameConfigChange.GameSection && section != GameConfigChange.EvolutionSection)
            return BadRequest(new { errors = new[] { $"section must be {GameConfigChange.GameSection} or {GameConfigChange.EvolutionSection}." } });
        return Ok(await store.GetHistoryAsync(gameId, section, Math.Clamp(count, 1, 200)));
    }


    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,63}$")]
    private static partial Regex GameIdPattern();

    public record GameBody(
        string? GameId,
        string? Name,
        GameType? Type,
        EngineKind? Engine,
        string? LaunchUrl,
        long? ProviderGameId,
        decimal MinBet,
        decimal MaxBet,
        decimal MaxWin,
        bool Enabled,
        RngSettings? Rng,
        LiveCrashSettings? Live,
        RouletteSettings? Roulette,
        TradingSettings? Trading,
        Domain.Blackjack.BlackjackSettings? Blackjack,
        string? ServerKey);

    public record UpdateEvolutionBody(List<TierBody>? Tiers);
    public record TierBody(int Level, string? Code, string? Name, decimal Multiplier);
}
