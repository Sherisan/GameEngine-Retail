using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Vault;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Vault;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for VAULT CRACKER (Engine = Vault). The game is the one the player's session token was launched
/// for — never a client parameter. Every call except info returns the dial: { round, fairness, history }.
///   GET  api/vault/info      limits, currency, payout ladder
///   GET  api/vault/state     the round in play (or the last one), the next seed hash, recent rounds
///   POST api/vault/bet       { stake }
///   POST api/vault/pick      { tumbler: 0-3 }
///   POST api/vault/intel     the Stethoscope
///   POST api/vault/cashout
///   POST api/vault/seed      { clientSeed } — between rounds only
/// </summary>
[ApiController]
[Route("api/vault")]
[Authorize]
public class VaultController(IGameRegistry games, VaultService vault) : ControllerBase
{
    public record BetBody(decimal Stake);
    public record PickBody(int Tumbler);
    public record SeedBody(string? ClientSeed);

    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;
        return Ok(new
        {
            game!.GameId, game.Name, game.MinBet, game.MaxBet, game.MaxWin, Currency = player!.Currency,
            VaultRules.Steps, VaultRules.Tumblers, VaultRules.Rtp,
            Ladder = Enumerable.Range(1, VaultRules.Steps).Select(VaultRules.Multiplier),
        });
    }

    [HttpGet("state")]
    public Task<IActionResult> State() => Run((g, p) => vault.StateAsync(g, p));

    [HttpPost("bet")]
    public Task<IActionResult> Bet([FromBody] BetBody body) => Run((g, p) => vault.BetAsync(g, p, body.Stake));

    [HttpPost("pick")]
    public Task<IActionResult> Pick([FromBody] PickBody body) => Run((g, p) => vault.PickAsync(g, p, body.Tumbler));

    [HttpPost("intel")]
    public Task<IActionResult> Intel() => Run((g, p) => vault.IntelAsync(g, p));

    [HttpPost("cashout")]
    public Task<IActionResult> CashOut() => Run((g, p) => vault.CashOutAsync(g, p));

    [HttpPost("seed")]
    public Task<IActionResult> Seed([FromBody] SeedBody body) => Run((g, p) => vault.SetClientSeedAsync(g, p, body.ClientSeed));

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
        if (game?.Engine != EngineKind.Vault)
            return (null, null, NotFound(new { error = "This game isn't VAULT CRACKER.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
