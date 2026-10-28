using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Presentation.DTOs;

namespace Worldiator.Server.Presentation.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameController : ControllerBase
{
    private readonly GameEngine _engine;
    private readonly IRoundRepository _rounds;
    private readonly IAsyncRoundRepository? _db;

    public GameController(GameEngine engine, IRoundRepository rounds, IAsyncRoundRepository? db = null)
    {
        _engine = engine;
        _rounds = rounds;
        _db = db;
    }

    [HttpGet("state")]
    public IActionResult GetState()
    {
        var round = _engine.CurrentRound;
        if (round == null) return Ok(new { phase = "waiting" });

        var dto = RoundStateDto.From(round, _engine.GetCurrentBets());
        return Ok(dto);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int count = 20)
    {
        var clamped = Math.Clamp(count, 1, 50);
        // Prefer DB (durable, survives restarts) — fall back to in-memory cache.
        var rounds = _db != null ? await _db.GetRecentAsync(clamped) : _rounds.GetRecent(clamped);
        return Ok(rounds.Select(HistoryEntryDto.From));
    }

    [HttpGet("limits")]
    public IActionResult GetLimits()
    {
        var config = _engine.Config;
        return Ok(new { minBet = config.MinBet, maxBet = config.MaxBet, maxWin = config.MaxWin });
    }

    /// <summary>The calling player's own bet history. Identity from JWT only — never a query param.</summary>
    [Authorize]
    [HttpGet("my-bets")]
    public async Task<IActionResult> GetMyBets([FromQuery] int count = 50)
    {
        var clamped = Math.Clamp(count, 1, 50);
        var playerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (playerId == null) return Unauthorized();

        var bets = _db != null ? await _db.GetBetsForPlayerAsync(playerId, clamped) : Array.Empty<Bet>();
        return Ok(bets.Select(b => new
        {
            b.RoundId,
            b.SlotIndex,
            b.Amount,
            b.Currency,
            b.CashedOutAt,
            b.WinAmount,
            b.PlacedAt,
        }));
    }

    /// <summary>Past round crash multipliers only — lighter than /history for a multiplier ticker.</summary>
    [HttpGet("multiplier-history")]
    public async Task<IActionResult> GetMultiplierHistory([FromQuery] int count = 50)
    {
        var clamped = Math.Clamp(count, 1, 50);
        var rounds = _db != null ? await _db.GetMultiplierHistoryAsync(clamped) : _rounds.GetRecent(clamped);
        return Ok(rounds.Select(HistoryEntryDto.From));
    }
}
