using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

/// <summary>
/// Sign-in for the Game Back Office. Accounts live in IBackOfficeUserStore with BCrypt password hashes.
/// A successful sign-in returns a JWT for the "BackOffice" scheme — same signing key as player tokens but its own
/// audience, so a player token never opens the back office and a back-office token never plays a game.
/// Every request re-checks the account (enabled, security stamp, current role), so disabling a user, changing their
/// role or resetting their password takes effect on their next call.
/// </summary>
public class BackOfficeAuthService
{
    public const string Scheme = "BackOffice";
    public const string Audience = "game-backoffice";
    public const string UserIdClaim = "uid";
    public const string StampClaim = "stamp";

    public const string ViewerPolicy = "BackOfficeViewer";
    public const string EditorPolicy = "BackOfficeEditor";
    public const string AdminPolicy = "BackOfficeAdmin";

    public const int MinPasswordLength = 10;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    // Lockout: this many failed sign-ins for one username (or from one IP) within the window locks it for the window.
    private const int MaxFailuresPerUser = 5;
    private const int MaxFailuresPerIp = 20;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    // Verified when the username doesn't exist, so a wrong username takes as long as a wrong password.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

    private readonly IBackOfficeUserStore _users;
    private readonly ILogger<BackOfficeAuthService> _logger;
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Failures(int Count, DateTime WindowStart);

    public BackOfficeAuthService(IBackOfficeUserStore users, IConfiguration config, ILogger<BackOfficeAuthService> logger)
    {
        _users = users;
        _logger = logger;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        _issuer = config["Jwt:Issuer"] ?? "worldiator-dev";
    }

    public static TokenValidationParameters ValidationParameters(IConfiguration config) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!)),
        ValidateIssuer = true,
        ValidIssuer = config["Jwt:Issuer"] ?? "worldiator-dev",
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role,
    };

    public static string? PasswordProblem(string? password, string username) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"Password must be at least {MinPasswordLength} characters."
            : password.Length > 128 ? "Password must be 128 characters or fewer."
            : string.Equals(password, username, StringComparison.OrdinalIgnoreCase) ? "Password can't be the username."
            : null;

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);

    public static bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);

    // ── Sign-in ─────────────────────────────────────────────────────────────

    public enum LoginOutcome { Ok, Invalid, LockedOut }

    public async Task<(LoginOutcome Outcome, string? Token, BackOfficeUser? User)> LoginAsync(string username, string password, string? ip)
    {
        username = username.Trim();
        var userKey = "u:" + username;
        var ipKey = "ip:" + (ip ?? "?");
        if (IsLocked(userKey, MaxFailuresPerUser) || IsLocked(ipKey, MaxFailuresPerIp))
        {
            _logger.LogWarning("Back office sign-in blocked (locked out): user={Username} ip={Ip}", username, ip);
            return (LoginOutcome.LockedOut, null, null);
        }

        var user = await _users.FindByUsernameAsync(username);
        var passwordOk = Verify(password, user?.PasswordHash ?? DummyHash);
        if (user == null || !passwordOk || !user.Enabled)
        {
            RecordFailure(userKey);
            RecordFailure(ipKey);
            _logger.LogWarning("Back office sign-in failed: user={Username} ip={Ip} reason={Reason}", username, ip,
                user == null ? "unknown user" : !passwordOk ? "wrong password" : "disabled");
            return (LoginOutcome.Invalid, null, null);
        }

        _failures.TryRemove(userKey, out _);
        await _users.RecordLoginAsync(user.UserId);
        _logger.LogInformation("Back office sign-in: user={Username} role={Role} ip={Ip}", user.Username, user.Role, ip);
        return (LoginOutcome.Ok, Issue(user), user);
    }

    private bool IsLocked(string key, int max) =>
        _failures.TryGetValue(key, out var f) && f.Count >= max && DateTime.UtcNow - f.WindowStart < FailureWindow;

    private void RecordFailure(string key) =>
        _failures.AddOrUpdate(key, _ => new Failures(1, DateTime.UtcNow),
            (_, f) => DateTime.UtcNow - f.WindowStart >= FailureWindow ? new Failures(1, DateTime.UtcNow) : f with { Count = f.Count + 1 });

    public string Issue(BackOfficeUser user)
    {
        var claims = new Claim[]
        {
            new(UserIdClaim, user.UserId.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(StampClaim, user.SecurityStamp),
        };
        var token = new JwtSecurityToken(_issuer, Audience, claims, expires: DateTime.UtcNow.Add(Lifetime),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Called for every authenticated back-office request: the account must still exist, be enabled and carry the
    /// same security stamp. Returns the principal with the account's current role, or null to reject the token.
    /// </summary>
    public async Task<ClaimsPrincipal?> RevalidateAsync(ClaimsPrincipal principal)
    {
        if (!int.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var userId)) return null;
        var user = await _users.FindAsync(userId);
        if (user == null || !user.Enabled || user.SecurityStamp != principal.FindFirst(StampClaim)?.Value) return null;

        var identity = new ClaimsIdentity(
            [
                new Claim(UserIdClaim, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(StampClaim, user.SecurityStamp),
            ],
            Scheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    public static int? UserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var id) ? id : null;

    // ── First admin ─────────────────────────────────────────────────────────

    /// <summary>
    /// When there are no accounts yet, creates an Admin from BackOffice:BootstrapAdmin:Username / Password.
    /// Does nothing once any account exists, so the setting can (and should) be removed after first sign-in.
    /// </summary>
    public async Task BootstrapAsync(IConfiguration config)
    {
        if ((await _users.ListAsync()).Count > 0) return;

        var username = config["BackOffice:BootstrapAdmin:Username"]?.Trim();
        var password = config["BackOffice:BootstrapAdmin:Password"];
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("No back office accounts exist. Set BackOffice:BootstrapAdmin:Username and :Password to create the first Admin.");
            return;
        }
        if (PasswordProblem(password, username) is { } problem)
        {
            _logger.LogError("BackOffice:BootstrapAdmin:Password is not acceptable: {Problem}", problem);
            return;
        }

        await _users.CreateAsync(username, username, Hash(password), BackOfficeRole.Admin);
        _logger.LogWarning("Created the first back office Admin \"{Username}\" from BackOffice:BootstrapAdmin — sign in, change the password and remove that setting.", username);
    }
}
