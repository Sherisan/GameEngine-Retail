using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Defuse;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Defuse;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for DEFUSE (Engine = Defuse). The game is the one the player's session token was launched
/// for — never a client parameter. Every call except info returns the board: { round, fairness, history }.
///   GET  api/defuse/info      limits, currency, payout ladder
///   GET  api/defuse/state     the round in play (or the last one), the next seed hash, recent rounds
///   POST api/defuse/bet       { stake }
///   POST api/defuse/cut       { wire: 0-5 }
///   POST api/defuse/tip
///   POST api/defuse/cashout
///   POST api/defuse/seed      { clientSeed } — between rounds only
/// </summary>
[ApiController]
[Route("api/defuse")]
[Authorize]
public class DefuseController(IGameRegistry games, DefuseService defuse) : ControllerBase
{
    public record BetBody(decimal Stake);
    public record CutBody(int Wire);
    public record SeedBody(string? ClientSeed);

    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        return Ok(new
        {
            game!.GameId, game.Name, game.MinBet, game.MaxBet, game.MaxWin, Currency = player!.Currency,
            DefuseRules.WireCount, DefuseRules.Rtp,
            Ladder = Enumerable.Range(1, DefuseRules.MaxRung).Select(DefuseRules.Multiplier),
        });
    }

    [HttpGet("state")]
    public Task<IActionResult> State() => Run((g, p) => defuse.StateAsync(g, p));

    [HttpPost("bet")]
    public Task<IActionResult> Bet([FromBody] BetBody body) => Run((g, p) => defuse.BetAsync(g, p, body.Stake));

    [HttpPost("cut")]
    public Task<IActionResult> Cut([FromBody] CutBody body) => Run((g, p) => defuse.CutAsync(g, p, body.Wire));

    [HttpPost("tip")]
    public Task<IActionResult> Tip() => Run((g, p) => defuse.TipAsync(g, p));

    [HttpPost("cashout")]
    public Task<IActionResult> CashOut() => Run((g, p) => defuse.CashOutAsync(g, p));

    [HttpPost("seed")]
    public Task<IActionResult> Seed([FromBody] SeedBody body) => Run((g, p) => defuse.SetClientSeedAsync(g, p, body.ClientSeed));

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
        if (game?.Engine != EngineKind.Defuse)
            return (null, null, NotFound(new { error = "This game isn't DEFUSE.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
