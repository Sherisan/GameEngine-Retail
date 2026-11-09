using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Fruit;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Fruit;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for JUICE RUSH (Engine = Fruit). The game is the one the player's session token was launched
/// for — never a client parameter. Every call except info returns the board: { round, fairness, history }.
///   GET  api/fruit/info      limits, currency, the fruit menu
///   GET  api/fruit/state     the round in play (or the last one), the next seed hash, recent rounds
///   POST api/fruit/bet       { stake }
///   POST api/fruit/slice     { fruit: "cherry" | "lemon" | "orange" | "coconut" | "watermelon" | "dragonfruit" }
///   POST api/fruit/cashout
///   POST api/fruit/seed      { clientSeed } — between rounds only
/// </summary>
[ApiController]
[Route("api/fruit")]
[Authorize]
public class FruitController(IGameRegistry games, FruitService juice) : ControllerBase
{
    public record BetBody(decimal Stake);
    public record SliceBody(string? Fruit);
    public record SeedBody(string? ClientSeed);

    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        return Ok(new
        {
            game!.GameId, game.Name, game.MinBet, game.MaxBet, game.MaxWin, Currency = player!.Currency,
            FruitRules.Rtp, FruitRules.MaxMultiplier,
            Fruits = FruitRules.Menu.Select(f => new { f.Kind, f.Name, f.Bombs, f.OutOf }),
        });
    }

    [HttpGet("state")]
    public Task<IActionResult> State() => Run((g, p) => juice.StateAsync(g, p));

    [HttpPost("bet")]
    public Task<IActionResult> Bet([FromBody] BetBody body) => Run((g, p) => juice.BetAsync(g, p, body.Stake));

    [HttpPost("slice")]
    public Task<IActionResult> Slice([FromBody] SliceBody body) => Run((g, p) => juice.SliceAsync(g, p, body.Fruit));

    [HttpPost("cashout")]
    public Task<IActionResult> CashOut() => Run((g, p) => juice.CashOutAsync(g, p));

    [HttpPost("seed")]
    public Task<IActionResult> Seed([FromBody] SeedBody body) => Run((g, p) => juice.SetClientSeedAsync(g, p, body.ClientSeed));

    private async Task<IActionResult> Run(Func<GameDefinition, PlayerSession, Task<(object? State, WalletResult? Error)>> call)
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        var (state, failure) = await call(game!, player!);
        return state != null ? Ok(state) : WalletController.ErrorResponse(this, failure!);
    }

    private (PlayerSession? Player, GameDefinition? Game, IActionResult? Error) Resolve()
    {
        var player = SessionTokenService.FromPrincipal(User);
        if (player == null) return (null, null, Unauthorized());
        var game = games.Find(player.GameId);
        if (game?.Engine != EngineKind.Fruit)
            return (null, null, NotFound(new { error = "This game isn't JUICE RUSH.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
