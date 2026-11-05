using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain.Promotions;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Game Back Office: promotions for the crash games (see Domain/Promotions/Promotion.cs). Viewers read; Editors and
/// Admins create, change, switch on / off, grant free bets and trigger rain. Changes are recorded under the signed-in
/// account and apply straight away on this instance (others within a few seconds).
/// </summary>
[ApiController]
[Route("api/backoffice/promotions")]
[Authorize(Policy = BackOfficeAuthService.ViewerPolicy)]
public partial class BackOfficePromotionsController(
    IPromoStore store, PromoEngine promos, IGameConfigStore gameConfig, ILogger<BackOfficePromotionsController> logger) : ControllerBase
{
    private string ChangedBy => User.Identity!.Name!;

    private Promotion? Find(int id) => store.Promotions.FirstOrDefault(p => p.PromotionId == id);

    // ── Read ────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var spent = await store.GetSpentAsync();
        var now = DateTime.UtcNow;
        return Ok(store.Promotions.OrderByDescending(p => p.PromotionId).Select(p => Summary(p, spent.GetValueOrDefault(p.PromotionId), now)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = Find(id);
        if (p == null) return NotFound(new { error = $"Promotion {id} doesn't exist." });
        var spent = (await store.GetSpentAsync()).GetValueOrDefault(id);
        return Ok(new { promotion = p, state = p.StateAt(DateTime.UtcNow, spent).ToString(), spent, awards = await store.CountAwardsAsync(id) });
    }

    /// <summary>Games a promotion can run in: the crash games whose engines report to the promo engine.</summary>
    [HttpGet("games")]
    public IActionResult EligibleGames() =>
        Ok(gameConfig.Games.Where(g => PromoEngine.SupportedEngines.Contains(g.Engine))
            .Select(g => new { g.GameId, g.Name, engine = g.Engine.ToString(), g.Enabled }));

    [HttpGet("{id:int}/awards")]
    public async Task<IActionResult> Awards(int id, [FromQuery] AwardStatus? status = null, [FromQuery] string? playerId = null, [FromQuery] int count = 200)
    {
        if (Find(id) == null) return NotFound(new { error = $"Promotion {id} doesn't exist." });
        return Ok(await store.ListAwardsAsync(id, string.IsNullOrWhiteSpace(playerId) ? null : playerId.Trim(), status, Math.Clamp(count, 1, 1000)));
    }

    /// <summary>Standings. period: omit for the current one, or a key such as "all", "2026-11-03", "2026-W45".</summary>
    [HttpGet("{id:int}/leaderboard")]
    public async Task<IActionResult> Leaderboard(int id, [FromQuery] string? period = null)
    {
        var p = Find(id);
        if (p is not { Type: PromoType.MultiplierLeaderboard }) return NotFound(new { error = $"Promotion {id} isn't a leaderboard." });
        var now = DateTime.UtcNow;
        var key = string.IsNullOrWhiteSpace(period) ? PromoEngine.PeriodKey(p.Leaderboard!.Period, now) : period.Trim();
        var top = await store.TopScoresAsync(id, key, 100);
        return Ok(new
        {
            period = key,
            endsAt = string.IsNullOrWhiteSpace(period) ? PromoEngine.PeriodEnds(p, now) : null,
            prizes = p.Leaderboard!.Prizes,
            rows = top.Select((s, i) => new { rank = i + 1, s.PlayerId, s.Username, s.GameId, s.Score, s.Stake, s.AchievedAt,
                prize = i < p.Leaderboard.Prizes.Count ? p.Leaderboard.Prizes[i] : (decimal?)null }),
        });
    }

    [HttpGet("{id:int}/history")]
    public async Task<IActionResult> History(int id, [FromQuery] int count = 50) =>
        Ok(await store.GetHistoryAsync(id, Math.Clamp(count, 1, 200)));

    // ── Write ───────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> Create([FromBody] Promotion body)
    {
        body.PromotionId = 0;
        if (Problem(body) is { } problem) return BadRequest(new { errors = new[] { problem } });
        var saved = await store.SaveAsync(body, ChangedBy);
        logger.LogInformation("Promotion {PromotionId} \"{Name}\" ({Type}) created by {ChangedBy}", saved.PromotionId, saved.Name, saved.Type, ChangedBy);
        return Ok(new { promotion = saved });
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> Update(int id, [FromBody] Promotion body)
    {
        var current = Find(id);
        if (current == null) return NotFound(new { error = $"Promotion {id} doesn't exist." });
        if (body.Type != current.Type)
            return BadRequest(new { errors = new[] { $"The type can't be changed (it is {current.Type}). Create a new promotion instead." } });
        body.PromotionId = id;
        if (Problem(body) is { } problem) return BadRequest(new { errors = new[] { problem } });
        var saved = await store.SaveAsync(body, ChangedBy);
        logger.LogInformation("Promotion {PromotionId} \"{Name}\" updated by {ChangedBy}", id, saved.Name, ChangedBy);
        return Ok(new { promotion = saved });
    }

    [HttpPost("{id:int}/enabled")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> SetEnabled(int id, [FromBody] EnabledBody body)
    {
        var current = Find(id);
        if (current == null) return NotFound(new { error = $"Promotion {id} doesn't exist." });
        var copy = Clone(current);
        copy.Enabled = body.Enabled;
        var saved = await store.SaveAsync(copy, ChangedBy);
        logger.LogInformation("Promotion {PromotionId} \"{Name}\" switched {State} by {ChangedBy}", id, saved.Name, body.Enabled ? "on" : "off", ChangedBy);
        return Ok(new { promotion = saved });
    }

    /// <summary>Gives free bets (BetsPerPlayer each, at the promotion's stake) to players by platform player id.</summary>
    [HttpPost("{id:int}/grant")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> Grant(int id, [FromBody] GrantBody body)
    {
        var p = Find(id);
        if (p is not { Type: PromoType.FreeBet }) return NotFound(new { error = $"Promotion {id} isn't a free bet promotion." });
        var ids = (body.PlayerIds ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(new { errors = new[] { "Enter at least one player id." } });
        if (ids.Count > 1000) return BadRequest(new { errors = new[] { "At most 1000 players at a time." } });
        var bad = ids.FirstOrDefault(x => !PlayerIdPattern().IsMatch(x));
        if (bad != null) return BadRequest(new { errors = new[] { $"\"{bad}\" isn't a valid player id." } });

        int granted = 0, outOfBudget = 0;
        var batch = PromoAward.NewId();
        foreach (var playerId in ids)
            for (var i = 1; i <= p.FreeBet!.BetsPerPlayer; i++)
            {
                var award = await promos.AwardFreeBetAsync(p, gameId: "", playerId, username: playerId, currency: "", p.FreeBet.Stake,
                    p.FreeBet.ValidDays, $"Granted by {ChangedBy}", PromoAward.IdFor("grant", p.PromotionId, batch, playerId, i), roundId: null);
                if (award != null) granted++;
                else outOfBudget++;
            }
        logger.LogInformation("Promotion {PromotionId}: {Granted} free bets granted to {Players} players by {ChangedBy} ({Skipped} over budget)",
            id, granted, ids.Count, ChangedBy, outOfBudget);
        return Ok(new { granted, outOfBudget });
    }

    /// <summary>Makes it rain now (it still only reaches players active within the window).</summary>
    [HttpPost("{id:int}/rain-now")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> RainNow(int id)
    {
        var p = Find(id);
        if (p is not { Type: PromoType.Rain }) return NotFound(new { error = $"Promotion {id} isn't a rain promotion." });
        if (!p.IsLiveAt(DateTime.UtcNow)) return BadRequest(new { errors = new[] { "The promotion isn't live." } });
        var reached = await promos.RainNowAsync(p, DateTime.UtcNow);
        logger.LogInformation("Promotion {PromotionId}: rain triggered by {ChangedBy}, reached {Players} players", id, ChangedBy, reached);
        return Ok(new { reached });
    }

    /// <summary>Withdraws a free bet that hasn't been played.</summary>
    [HttpPost("awards/{awardId}/cancel")]
    [Authorize(Policy = BackOfficeAuthService.EditorPolicy)]
    public async Task<IActionResult> CancelAward(string awardId)
    {
        var award = await store.GetAwardAsync(awardId);
        if (award == null) return NotFound(new { error = "No such award." });
        if (award is not { Kind: AwardKind.FreeBet, Status: AwardStatus.Available })
            return BadRequest(new { errors = new[] { "Only a free bet that hasn't been played can be cancelled." } });
        award.Status = AwardStatus.Cancelled;
        award.SettledAt = DateTime.UtcNow;
        award.Error = $"Cancelled by {ChangedBy}";
        await store.UpdateAwardAsync(award);
        logger.LogInformation("Free bet {AwardId} for {PlayerId} cancelled by {ChangedBy}", awardId, award.PlayerId, ChangedBy);
        return Ok(new { award });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string? Problem(Promotion p)
    {
        p.Name = p.Name?.Trim() ?? "";
        p.Terms = p.Terms?.Trim() ?? "";
        p.GameIds = (p.GameIds ?? []).Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        p.StartsAt = AsUtc(p.StartsAt);
        p.EndsAt = AsUtc(p.EndsAt);
        try { p.Validate(); }
        catch (InvalidOperationException ex) { return ex.Message; }

        foreach (var gameId in p.GameIds)
        {
            var game = gameConfig.Games.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (game == null) return $"Unknown game \"{gameId}\".";
            if (!Promotion.EnginesFor(p.Type).Contains(game.Engine))
                return $"{game.Name} ({game.Engine} engine) can't run a {p.Type} promotion — it suits {string.Join(", ", Promotion.EnginesFor(p.Type).Select(EngineLabel).Distinct())} games.";
        }
        // Store the catalogue's casing so the engines' lookups match.
        p.GameIds = p.GameIds.Select(id => gameConfig.Games.First(g => string.Equals(g.GameId, id, StringComparison.OrdinalIgnoreCase)).GameId).ToList();
        return null;
    }

    private static string EngineLabel(Domain.EngineKind e) => e switch
    {
        Domain.EngineKind.Custom or Domain.EngineKind.LiveCrash or Domain.EngineKind.MarginCall => "crash",
        Domain.EngineKind.Instant => "card / instant",
        _ => e.ToString().ToLowerInvariant(),
    };

    private static DateTime? AsUtc(DateTime? d) => d switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    };

    private static object Summary(Promotion p, decimal spent, DateTime now) => new
    {
        p.PromotionId, p.Name, type = p.Type.ToString(), p.Enabled, p.GameIds, p.StartsAt, p.EndsAt, p.Budget, spent,
        state = p.StateAt(now, spent).ToString(),
    };

    private static Promotion Clone(Promotion p) =>
        System.Text.Json.JsonSerializer.Deserialize<Promotion>(System.Text.Json.JsonSerializer.Serialize(p, Domain.GameCatalogue.Json), Domain.GameCatalogue.Json)!;

    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,64}$")]
    private static partial Regex PlayerIdPattern();

    public record EnabledBody(bool Enabled);
    public record GrantBody(List<string>? PlayerIds);
}
