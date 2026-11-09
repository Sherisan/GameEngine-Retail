using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Dive;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Dive;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for DEEP DIVE (Engine = Dive). The game is the one the player's session token was launched
/// for — never a client parameter. Every call except info returns the ocean: { round, fairness, history }.
///   GET  api/dive/info      limits, currency, payout ladder
///   GET  api/dive/state     the dive in progress (or the last one), the next seed hash, recent dives
///   POST api/dive/bet       { stake }
///   POST api/dive/pick      { cave: 0-2 }
///   POST api/dive/cashout   surface
///   POST api/dive/seed      { clientSeed } — between dives only
/// </summary>
[ApiController]
[Route("api/dive")]
[Authorize]
public class DiveController(IGameRegistry games, DiveService dive) : ControllerBase
{
    public record BetBody(decimal Stake);
    public record PickBody(int Cave);
    public record SeedBody(string? ClientSeed);

    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        return Ok(new
        {
            game!.GameId, game.Name, game.MinBet, game.MaxBet, game.MaxWin, Currency = player!.Currency,
            DiveRules.Zones, DiveRules.Caves, DiveRules.Rtp,
            Ladder = Enumerable.Range(1, DiveRules.Zones).Select(DiveRules.Multiplier),
        });
    }

    [HttpGet("state")]
    public Task<IActionResult> State() => Run((g, p) => dive.StateAsync(g, p));

    [HttpPost("bet")]
    public Task<IActionResult> Bet([FromBody] BetBody body) => Run((g, p) => dive.BetAsync(g, p, body.Stake));

    [HttpPost("pick")]
    public Task<IActionResult> Pick([FromBody] PickBody body) => Run((g, p) => dive.PickAsync(g, p, body.Cave));

    [HttpPost("cashout")]
    public Task<IActionResult> CashOut() => Run((g, p) => dive.CashOutAsync(g, p));

    [HttpPost("seed")]
    public Task<IActionResult> Seed([FromBody] SeedBody body) => Run((g, p) => dive.SetClientSeedAsync(g, p, body.ClientSeed));

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
        if (game?.Engine != EngineKind.Dive)
            return (null, null, NotFound(new { error = "This game isn't DEEP DIVE.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
