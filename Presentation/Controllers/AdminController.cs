using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly IRoundRepository _cache;
    private readonly IAsyncRoundRepository? _db;
    private readonly GameEngine _engine;
    private readonly string _apiKey;

    public AdminController(IRoundRepository cache, GameEngine engine, IConfiguration config, IAsyncRoundRepository? db = null)
    {
        _cache = cache;
        _engine = engine;
        _db = db;
        _apiKey = config["GamesApi:ApiKey"] ?? string.Empty;
    }

    private IActionResult? CheckApiKey()
    {
        var incoming = Request.Headers["X-API-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(_apiKey) || incoming != _apiKey)
            return Unauthorized(new { error = "Invalid or missing X-API-Key" });
        return null;
    }

    [HttpGet("limits")]
    public IActionResult GetLimits()
    {
        if (CheckApiKey() is { } err) return err;
        var c = _engine.Config;
        return Ok(new { minBet = c.MinBet, maxBet = c.MaxBet, maxWin = c.MaxWin });
    }

    [HttpPut("limits")]
    public IActionResult UpdateLimits([FromBody] UpdateLimitsRequest req)
    {
        if (CheckApiKey() is { } err) return err;
        if (req.MinBet <= 0 || req.MaxBet <= req.MinBet || req.MaxWin <= 0)
            return BadRequest("Invalid limits: MinBet > 0, MaxBet > MinBet, MaxWin > 0");

        _engine.UpdateLimits(req.MinBet, req.MaxBet, req.MaxWin);
        return Ok(new { minBet = req.MinBet, maxBet = req.MaxBet, maxWin = req.MaxWin });
    }

    public record UpdateLimitsRequest(decimal MinBet, decimal MaxBet, decimal MaxWin);

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (CheckApiKey() is { } err) return err;
        // Prefer DB (authoritative, survives restarts) — fall back to in-memory cache.
        IReadOnlyList<Round> rounds = _db != null
            ? await _db.GetRecentAsync(50)
            : _cache.GetRecent(50);

        int totalBets = 0;
        decimal totalWagered = 0m, totalPaidOut = 0m;
        foreach (var r in rounds)
            foreach (var b in r.Bets)
            {
                totalBets++;
                totalWagered += b.Amount;
                totalPaidOut += b.WinAmount ?? 0m;
            }

        return Ok(new
        {
            totalRounds = rounds.Count,
            averageCrash = rounds.Count > 0 ? rounds.Average(r => (double)r.CrashMultiplier) : 0,
            highestCrash = rounds.Count > 0 ? (double)rounds.Max(r => r.CrashMultiplier) : 0,
            totalBets,
            totalWagered,
            totalPaidOut,
        });
    }

    [HttpGet("rounds")]
    public async Task<IActionResult> GetRounds([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (CheckApiKey() is { } err) return err;
        IReadOnlyList<Round> rounds = _db != null
            ? await _db.GetRecentAsync(50)
            : _cache.GetRecent(50);

        var paged = rounds
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.RoundId,
                r.CrashMultiplier,
                r.StartedAt,
                r.CrashedAt,
                betCount = r.Bets.Count,
                totalWagered = r.Bets.Sum(b => b.Amount),
                totalPaidOut = r.Bets.Sum(b => b.WinAmount ?? 0),
            });

        return Ok(paged);
    }
}
