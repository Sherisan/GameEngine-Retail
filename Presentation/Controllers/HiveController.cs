using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Hive;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Hive;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for HIVE MIND (Engine = Hive). The game is the one the player's session token was launched
/// for — never a client parameter. Every call except info returns the hive: { round, fairness, history }.
///   GET  api/hive/info      limits, currency, bee levels
///   GET  api/hive/state     the round in play (or the last one), the next seed hash, recent rounds
///   POST api/hive/bet       { stake, bees: 3 | 5 | 7 | 10 }
///   POST api/hive/pick      { cell: 0-18 }
///   POST api/hive/cashout
///   POST api/hive/seed      { clientSeed } — between rounds only
/// </summary>
[ApiController]
[Route("api/hive")]
[Authorize]
public class HiveController(IGameRegistry games, HiveService hive) : ControllerBase
{
    public record BetBody(decimal Stake, int Bees);
    public record PickBody(int Cell);
    public record SeedBody(string? ClientSeed);

    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        return Ok(new
        {
            game!.GameId, game.Name, game.MinBet, game.MaxBet, game.MaxWin, Currency = player!.Currency,
            HiveRules.Rtp, HiveRules.MaxMultiplier, HiveRules.Levels, Cells = HiveBoard.CellCount,
        });
    }

    [HttpGet("state")]
    public Task<IActionResult> State() => Run((g, p) => hive.StateAsync(g, p));

    [HttpPost("bet")]
    public Task<IActionResult> Bet([FromBody] BetBody body) => Run((g, p) => hive.BetAsync(g, p, body.Stake, body.Bees));

    [HttpPost("pick")]
    public Task<IActionResult> Pick([FromBody] PickBody body) => Run((g, p) => hive.PickAsync(g, p, body.Cell));

    [HttpPost("cashout")]
    public Task<IActionResult> CashOut() => Run((g, p) => hive.CashOutAsync(g, p));

    [HttpPost("seed")]
    public Task<IActionResult> Seed([FromBody] SeedBody body) => Run((g, p) => hive.SetClientSeedAsync(g, p, body.ClientSeed));

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
        if (game?.Engine != EngineKind.Hive)
            return (null, null, NotFound(new { error = "This game isn't HIVE MIND.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
