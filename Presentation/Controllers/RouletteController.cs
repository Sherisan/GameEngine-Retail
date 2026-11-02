using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Roulette;

namespace Worldiator.Server.Presentation.Controllers;

/// <summary>
/// Frontend-facing API for on-demand roulette (Engine = Roulette). The game is the one the player's session
/// token was launched for — never a client parameter. Balance is read from api/wallet/balance as for other games.
/// </summary>
[ApiController]
[Route("api/roulette")]
[Authorize]
public class RouletteController(IGameRegistry games, IGameRngService rng, RouletteService roulette) : ControllerBase
{
    /// <summary>What the table needs: payouts, limits, currency, fairness.</summary>
    [HttpGet("info")]
    public IActionResult Info()
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;

        var settings = game!.Roulette ?? new RouletteSettings();
        var (hash, previous) = rng.GetSeedInfo(game);
        return Ok(new
        {
            game.GameId,
            game.Name,
            Type     = game.Type.ToString(),
            game.MinBet,
            game.MaxBet,
            game.MaxWin,
            Rtp      = Math.Round(game.Rng!.Rtp, 4),
            Currency = player!.Currency,
            settings.MaxBetsPerSpin,
            Payouts  = RouletteTable.Payouts,
            Limits   = RouletteTable.Payouts.Keys.ToDictionary(t => t, t => settings.LimitFor(t, game.MaxBet)),
            Fairness = new { CurrentServerSeedHash = hash, PreviousServerSeed = previous },
        });
    }

    /// <summary>Places the bets and spins: debits the total, draws the pocket, credits the win.</summary>
    [HttpPost("spin")]
    public async Task<IActionResult> Spin([FromBody] RouletteSpinRequest body)
    {
        var (player, game, error) = Resolve();
        if (error != null) return error;

        var (result, failure) = await roulette.SpinAsync(game!, player!, body);
        return result != null ? Ok(result) : WalletController.ErrorResponse(this, failure!);
    }

    private (PlayerSession? Player, GameDefinition? Game, IActionResult? Error) Resolve()
    {
        var player = SessionTokenService.FromPrincipal(User);
        if (player == null) return (null, null, Unauthorized());

        var game = games.Find(player.GameId);
        if (game?.Rng == null || game.Engine != EngineKind.Roulette)
            return (null, null, NotFound(new { error = "This game isn't roulette.", code = "NOT_PLAYABLE" }));
        return (player, game, null);
    }
}
