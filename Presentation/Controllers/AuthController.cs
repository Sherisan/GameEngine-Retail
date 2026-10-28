using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Called by Games-API to create a session token for a verified player.
/// Not exposed to end users — protected by a shared API key.
/// Kept for existing Feeding Frenzy integrations; new games should use POST api/games/launch.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly SessionTokenService _tokens;
    private readonly IGameRegistry _games;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, SessionTokenService tokens, IGameRegistry games, ILogger<AuthController> logger)
    {
        _config = config;
        _tokens = tokens;
        _games  = games;
        _logger = logger;
    }

    [HttpPost("create-session")]
    public IActionResult CreateSession([FromBody] CreateSessionBody body)
    {
        if (!GamesApiKey.IsValid(Request, _config))
        {
            _logger.LogWarning("Worldiator CreateSession rejected — invalid API key");
            return Unauthorized();
        }

        if (body.PlayerID <= 0 || string.IsNullOrWhiteSpace(body.CurrencyCode))
            return BadRequest("PlayerID and CurrencyCode are required.");

        // Games-API sends the catalogue's numeric ProviderGameID; our own game id is also accepted.
        var gameId = string.IsNullOrWhiteSpace(body.GameId) ? SessionTokenService.DefaultGameId
            : long.TryParse(body.GameId, out var providerGameId) ? _games.FindByProviderGameId(providerGameId)?.GameId ?? body.GameId
            : body.GameId;
        if (gameId != SessionTokenService.DefaultGameId && _games.Find(gameId) == null)
            return NotFound($"Unknown or disabled game \"{gameId}\".");

        var username = string.IsNullOrWhiteSpace(body.Username) ? "Player" : body.Username;
        return Ok(new { token = _tokens.Issue(body.PlayerID.ToString(), username, body.CurrencyCode, gameId) });
    }

    public record CreateSessionBody(long PlayerID, long OperatorID, string CurrencyCode, string Username, string? GameId = null);
}

/// <summary>Checks the X-API-Key header against GamesApi:ApiKey (the Games-API → this API shared secret).</summary>
public static class GamesApiKey
{
    public static bool IsValid(HttpRequest request, IConfiguration config)
    {
        var expected = config["GamesApi:ApiKey"];
        var inbound  = request.Headers["X-API-Key"].FirstOrDefault();
        return !string.IsNullOrEmpty(expected) && inbound != null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(inbound));
    }
}
