using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>Back-office account management — Admins only.</summary>
[ApiController]
[Route("api/backoffice/users")]
[Authorize(Policy = BackOfficeAuthService.AdminPolicy)]
public partial class BackOfficeUsersController(IBackOfficeUserStore users, ILogger<BackOfficeUsersController> logger) : ControllerBase
{
    public static object Dto(BackOfficeUser u) => new { u.UserId, u.Username, u.DisplayName, u.Role, u.Enabled, u.CreatedAt, u.LastLoginAt };

    private string Me => User.Identity!.Name!;

    [HttpGet]
    public async Task<IActionResult> List() => Ok((await users.ListAsync()).Select(Dto));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBody body)
    {
        var username = body.Username?.Trim() ?? "";
        if (!UsernamePattern().IsMatch(username))
            return BadRequest(new { error = "Username must be 3-64 characters: letters, digits, '.', '_', '-' or '@'." });
        if (body.Role == null) return BadRequest(new { error = "Role is required (Viewer, Editor or Admin)." });
        if (BackOfficeAuthService.PasswordProblem(body.Password, username) is { } problem) return BadRequest(new { error = problem });
        var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? username : body.DisplayName.Trim();
        if (displayName.Length > 100) return BadRequest(new { error = "Display name must be 100 characters or fewer." });

        var user = await users.CreateAsync(username, displayName, BackOfficeAuthService.Hash(body.Password!), body.Role.Value);
        if (user == null) return Conflict(new { error = $"Username \"{username}\" is taken." });
        logger.LogInformation("Back office user {Username} ({Role}) created by {Admin}", user.Username, user.Role, Me);
        return Ok(Dto(user));
    }

    /// <summary>Changes display name, role and enabled. A role change or disable signs the user out everywhere.</summary>
    [HttpPut("{userId:int}")]
    public async Task<IActionResult> Update(int userId, [FromBody] UpdateBody body)
    {
        var user = await users.FindAsync(userId);
        if (user == null) return NotFound(new { error = "No such user." });
        var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? user.DisplayName : body.DisplayName.Trim();
        if (displayName.Length > 100) return BadRequest(new { error = "Display name must be 100 characters or fewer." });
        var role = body.Role ?? user.Role;
        var enabled = body.Enabled ?? user.Enabled;

        if (user.UserId == BackOfficeAuthService.UserId(User) && (!enabled || role != BackOfficeRole.Admin))
            return BadRequest(new { error = "You can't disable your own account or remove your own Admin role." });
        if (user.Role == BackOfficeRole.Admin && user.Enabled && (!enabled || role != BackOfficeRole.Admin)
            && (await users.ListAsync()).Count(u => u.Role == BackOfficeRole.Admin && u.Enabled) <= 1)
            return BadRequest(new { error = "This is the last enabled Admin." });

        var signOut = role != user.Role || enabled != user.Enabled;
        user = user with
        {
            DisplayName = displayName,
            Role = role,
            Enabled = enabled,
            SecurityStamp = signOut ? BackOfficeStamp.New() : user.SecurityStamp,
        };
        await users.UpdateAsync(user);
        logger.LogInformation("Back office user {Username} updated by {Admin}: role={Role} enabled={Enabled}", user.Username, Me, role, enabled);
        return Ok(Dto(user));
    }

    /// <summary>Sets a new password for a user (e.g. they forgot theirs) and signs them out everywhere.</summary>
    [HttpPost("{userId:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int userId, [FromBody] ResetPasswordBody body)
    {
        var user = await users.FindAsync(userId);
        if (user == null) return NotFound(new { error = "No such user." });
        if (BackOfficeAuthService.PasswordProblem(body.Password, user.Username) is { } problem) return BadRequest(new { error = problem });

        await users.UpdateAsync(user with { PasswordHash = BackOfficeAuthService.Hash(body.Password!), SecurityStamp = BackOfficeStamp.New() });
        logger.LogInformation("Back office user {Username} password reset by {Admin}", user.Username, Me);
        return Ok(Dto(user));
    }

    [GeneratedRegex("^[A-Za-z0-9._@-]{3,64}$")]
    private static partial Regex UsernamePattern();

    public record CreateBody(string? Username, string? DisplayName, string? Password, BackOfficeRole? Role);
    public record UpdateBody(string? DisplayName, BackOfficeRole? Role, bool? Enabled);
    public record ResetPasswordBody(string? Password);
}
