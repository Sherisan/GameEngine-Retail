using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Blackjack;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Blackjack;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for blackjack (Engine = Blackjack). The game is the one the player's session token was launched
/// for — never a client parameter. Every call returns the table state: { round, nextServerSeedHash }.
///   GET  api/blackjack/info     rules, limits, currency
///   GET  api/blackjack/state    the round in play (or the last one), and the next round's server seed hash
///   POST api/blackjack/deal     { stake, freeBet?, clientSeed? }
///   POST api/blackjack/action   { action: hit | stand | double | split }
/// </summary>
[ApiController]
[Route("api/blackjack")]
[Authorize]
public class BlackjackController(IGameRegistry games, BlackjackService blackjack) : ControllerBase
{
    public record DealBody(decimal Stake, bool FreeBet = false, string? ClientSeed = null);
    public record ActionBody(string? Action);

    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        var rules = game!.Blackjack ?? new BlackjackSettings();
        return Ok(new
        {
            game.GameId, game.Name, game.MinBet, game.MaxBet, game.MaxWin, Currency = player!.Currency,
            rules.Decks, rules.DealerHitsSoft17, rules.BlackjackPays, rules.DoubleAfterSplit, rules.MaxHands,
        });
    }

    [HttpGet("state")]
    public async Task<IActionResult> State()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        return Ok(await blackjack.StateAsync(game!, player!));
    }

    [HttpPost("deal")]
    public async Task<IActionResult> Deal([FromBody] DealBody body)
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        var (state, failure) = await blackjack.DealAsync(game!, player!, body.Stake, body.FreeBet, body.ClientSeed);
        return state != null ? Ok(state) : WalletController.ErrorResponse(this, failure!);
    }

    [HttpPost("action")]
    public async Task<IActionResult> Act([FromBody] ActionBody body)
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        var (state, failure) = await blackjack.ActAsync(game!, player!, body.Action ?? "");
        return state != null ? Ok(state) : WalletController.ErrorResponse(this, failure!);
    }

    private (PlayerSession? Player, GameDefinition? Game, IActionResult? Error) Resolve()
    {
        var player = SessionTokenService.FromPrincipal(User);
        if (player == null) return (null, null, Unauthorized());
        var game = games.Find(player.GameId);
        if (game?.Engine != EngineKind.Blackjack)
            return (null, null, NotFound(new { error = "This game isn't blackjack.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
