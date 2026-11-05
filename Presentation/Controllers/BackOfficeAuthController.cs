using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>Sign-in for the Game Back Office. See BackOfficeAuthService.</summary>
[ApiController]
[Route("api/backoffice/auth")]
public class BackOfficeAuthController(BackOfficeAuthService auth, IBackOfficeUserStore users) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
            return BadRequest(new { error = "Username and password are required." });

        var (outcome, token, user) = await auth.LoginAsync(body.Username, body.Password, HttpContext.Connection.RemoteIpAddress?.ToString());
        return outcome switch
        {
            BackOfficeAuthService.LoginOutcome.Ok => Ok(new { token, user = BackOfficeUsersController.Dto(user!) }),
            BackOfficeAuthService.LoginOutcome.LockedOut => StatusCode(429, new { error = "Too many failed sign-ins. Try again in 15 minutes." }),
            _ => Unauthorized(new { error = "Wrong username or password." }),
        };
    }

    [HttpGet("me")]
    [Authorize(Policy = BackOfficeAuthService.ViewerPolicy)]
    public async Task<IActionResult> Me()
    {
        var user = await users.FindAsync(BackOfficeAuthService.UserId(User)!.Value);
        return user == null ? Unauthorized() : Ok(BackOfficeUsersController.Dto(user));
    }

    /// <summary>Changes your own password. Signs out your other sessions; returns a fresh token for this one.</summary>
    [HttpPost("change-password")]
    [Authorize(Policy = BackOfficeAuthService.ViewerPolicy)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordBody body)
    {
        var user = await users.FindAsync(BackOfficeAuthService.UserId(User)!.Value);
        if (user == null) return Unauthorized();
        if (string.IsNullOrEmpty(body.CurrentPassword) || !BackOfficeAuthService.Verify(body.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is wrong." });
        if (BackOfficeAuthService.PasswordProblem(body.NewPassword, user.Username) is { } problem)
            return BadRequest(new { error = problem });

        user = user with { PasswordHash = BackOfficeAuthService.Hash(body.NewPassword!), SecurityStamp = BackOfficeStamp.New() };
        await users.UpdateAsync(user);
        return Ok(new { token = auth.Issue(user), user = BackOfficeUsersController.Dto(user) });
    }

    public record LoginBody(string? Username, string? Password);
    public record ChangePasswordBody(string? CurrentPassword, string? NewPassword);
}
