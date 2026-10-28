using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Shared wallet for every game on this API.
///
/// Two callers, two kinds of auth:
///   - The player's browser: GET balance with its session token as a Bearer header. Read-only.
///   - The game's own backend: POST balance/debit/credit/rollback with X-Game-Key (the game's ServerKey)
///     and the player's session token in the body. Money only moves from here, because the game result
///     must be decided server-side — a credit endpoint the browser can call lets players pay themselves.
/// </summary>
[ApiController]
[Route("api/wallet")]
public class WalletController(IGameRegistry games, SessionTokenService tokens, WalletService wallet) : ControllerBase
{
    public const string GameKeyHeader = "X-Game-Key";

    /// <summary>Player's own balance. Identity from the JWT only.</summary>
    [Authorize]
    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance()
    {
        var player = SessionTokenService.FromPrincipal(User);
        if (player == null) return Unauthorized();
        return Ok(new { balance = await wallet.GetBalanceAsync(player), currency = player.Currency });
    }

    [AllowAnonymous]
    [HttpPost("balance")]
    public async Task<IActionResult> GetBalanceForGame([FromBody] SessionBody body)
    {
        var (game, player, error) = Authenticate(body.SessionToken, allowExpired: false);
        if (error != null) return error;
        return Ok(new { balance = await wallet.GetBalanceAsync(player!), currency = player!.Currency });
    }

    [AllowAnonymous]
    [HttpPost("debit")]
    public async Task<IActionResult> Debit([FromBody] MoveBody body)
    {
        // Placing a bet needs a live session.
        var (game, player, error) = Authenticate(body.SessionToken, allowExpired: false);
        if (error != null) return error;
        return ToResponse(await wallet.DebitAsync(game!, player!, body.RoundId, body.TransactionId, body.Amount), player!);
    }

    [AllowAnonymous]
    [HttpPost("credit")]
    public async Task<IActionResult> Credit([FromBody] MoveBody body)
    {
        // Settling a round the player already paid into must work even if the session lapsed mid-round;
        // the ledger only allows a credit against a completed debit.
        var (game, player, error) = Authenticate(body.SessionToken, allowExpired: true);
        if (error != null) return error;
        return ToResponse(await wallet.CreditAsync(game!, player!, body.RoundId, body.TransactionId, body.Amount), player!);
    }

    [AllowAnonymous]
    [HttpPost("rollback")]
    public async Task<IActionResult> Rollback([FromBody] RollbackBody body)
    {
        var (game, player, error) = Authenticate(body.SessionToken, allowExpired: true);
        if (error != null) return error;
        return ToResponse(await wallet.RollbackAsync(game!, player!, body.TransactionId, body.DebitTransactionId), player!);
    }

    private (GameDefinition? Game, PlayerSession? Player, IActionResult? Error) Authenticate(string sessionToken, bool allowExpired)
    {
        var game = games.FindByServerKey(Request.Headers[GameKeyHeader].ToString());
        if (game == null)
            return (null, null, Unauthorized(new { error = "Invalid X-Game-Key.", code = "INVALID_GAME_KEY" }));

        var player = tokens.Validate(sessionToken, allowExpired);
        if (player == null)
            return (null, null, Unauthorized(new { error = "Invalid or expired session token.", code = "INVALID_SESSION" }));

        // A session launched for one game can't be spent by another game's backend.
        if (!string.Equals(player.GameId, game.GameId, StringComparison.OrdinalIgnoreCase))
            return (null, null, StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Session token belongs to a different game.", code = "WRONG_GAME" }));

        return (game, player, null);
    }

    private IActionResult ToResponse(WalletResult result, PlayerSession player) =>
        result.Ok
            ? Ok(new { transactionId = result.TransactionId, balance = result.Balance, currency = player.Currency, replayed = result.Replayed })
            : ErrorResponse(this, result);

    /// <summary>Maps a failed WalletResult to its HTTP status. Shared with PlayController.</summary>
    public static IActionResult ErrorResponse(ControllerBase c, WalletResult result)
    {
        var body = new { error = result.Error, code = result.ErrorCode };
        return result.ErrorKind switch
        {
            WalletErrorKind.BadRequest        => c.BadRequest(body),
            WalletErrorKind.Conflict          => c.Conflict(body),
            WalletErrorKind.Rejected          => c.UnprocessableEntity(body),
            _                                 => c.StatusCode(StatusCodes.Status502BadGateway, body),
        };
    }

    public record SessionBody(string SessionToken);
    public record MoveBody(string SessionToken, string RoundId, string TransactionId, decimal Amount);
    public record RollbackBody(string SessionToken, string TransactionId, string DebitTransactionId);
}
