using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>Game catalogue and launch for every game hosted on this API (crash, slot, prediction).</summary>
[ApiController]
[Route("api/games")]
[AllowAnonymous]
public class GamesController(IGameRegistry games, SessionTokenService tokens, IConfiguration config, ILogger<GamesController> logger)
    : ControllerBase
{
    /// <summary>Public list of enabled games — safe for a lobby.</summary>
    [HttpGet]
    public IActionResult List() =>
        Ok(games.All.Where(g => g.Enabled).Select(g => new
        {
            g.GameId,
            g.Name,
            Type = g.Type.ToString(),
            // Null when the game doesn't play through api/play (own engine or own backend).
            Engine = g.Engine.ToString(),
            Model = g.Rng?.Model.ToString(),
            Rtp = g.Rng == null ? (decimal?)null : Math.Round(g.Rng.Rtp, 4),
            g.MinBet,
            g.MaxBet,
            g.MaxWin,
        }));

    /// <summary>
    /// Called by Games-API (X-API-Key) when a player opens a game. Returns a session token scoped to that
    /// game and, when the game has a LaunchUrl, the URL to send the player to (token appended as ?token=).
    /// </summary>
    [HttpPost("launch")]
    public IActionResult Launch([FromBody] LaunchBody body)
    {
        if (!GamesApiKey.IsValid(Request, config))
        {
            logger.LogWarning("Game launch rejected — invalid API key");
            return Unauthorized();
        }

        var game = games.Find(body.GameId);
        if (game == null)
            return NotFound(new { error = $"Unknown or disabled game \"{body.GameId}\"." });
        if (body.PlayerID <= 0 || string.IsNullOrWhiteSpace(body.CurrencyCode))
            return BadRequest(new { error = "PlayerID and CurrencyCode are required." });

        var username = string.IsNullOrWhiteSpace(body.Username) ? "Player" : body.Username;
        var token    = tokens.Issue(body.PlayerID.ToString(), username, body.CurrencyCode, game.GameId);
        var url      = string.IsNullOrEmpty(game.LaunchUrl) ? null : QueryHelpers.AddQueryString(game.LaunchUrl, "token", token);

        return Ok(new { token, gameId = game.GameId, launchUrl = url });
    }

    public record LaunchBody(string GameId, long PlayerID, long OperatorID, string CurrencyCode, string? Username);
}
