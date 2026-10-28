using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Worldiator.Server.Application;

/// <summary>Player identity carried in a session token. Never taken from client-supplied parameters.</summary>
public record PlayerSession(string PlayerId, string Username, string Currency, string GameId);

/// <summary>
/// Mints and validates the player session JWTs shared by every game.
/// Uses the same Jwt:* settings as the bearer middleware in Program.cs.
/// </summary>
public class SessionTokenService
{
    /// <summary>Tokens minted before multi-game support carry no "game" claim — they belong to Feeding Frenzy.</summary>
    public const string DefaultGameId = "feeding-frenzy";
    public const string GameClaim = "game";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;

    public SessionTokenService(IConfiguration config)
    {
        var jwtKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey))
            throw new InvalidOperationException("Jwt:Key not configured");
        _key      = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        _issuer   = config["Jwt:Issuer"]   ?? "worldiator-dev";
        _audience = config["Jwt:Audience"] ?? "worldiator-game";
    }

    public string Issue(string playerId, string username, string currency, string gameId)
    {
        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub,  playerId),
            new(JwtRegisteredClaimNames.Name, username),
            new("currency", currency),
            new(GameClaim, gameId),
        };
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_issuer, _audience, claims,
                        expires: DateTime.UtcNow.Add(Lifetime), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Validates a token a game backend forwarded on the player's behalf. Returns null if invalid.
    /// allowExpired lets a round that started inside the session still be settled after the token lapses.
    /// </summary>
    public PlayerSession? Validate(string token, bool allowExpired = false)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = _key,
                ValidateIssuer           = true,
                ValidIssuer              = _issuer,
                ValidateAudience         = true,
                ValidAudience            = _audience,
                ValidateLifetime         = !allowExpired,
                ClockSkew                = TimeSpan.FromSeconds(30),
            }, out _);
            return FromPrincipal(principal);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    // 'sub' may arrive mapped (ClaimTypes.NameIdentifier) or raw, depending on the handler's claim mapping.
    public static PlayerSession? FromPrincipal(ClaimsPrincipal user)
    {
        var playerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(playerId)) return null;

        var username = user.FindFirst(ClaimTypes.Name)?.Value
                    ?? user.FindFirst(JwtRegisteredClaimNames.Name)?.Value
                    ?? "Player";
        var currency = user.FindFirst("currency")?.Value ?? "ZAR";
        var gameId   = user.FindFirst(GameClaim)?.Value ?? DefaultGameId;
        return new PlayerSession(playerId, username, currency, gameId);
    }
}
