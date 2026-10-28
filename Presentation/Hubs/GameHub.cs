using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Worldiator.Server.Application;
using Worldiator.Server.Contracts;
using Worldiator.Server.Presentation.DTOs;

namespace Worldiator.Server.Presentation.Hubs;

[Authorize]
public class GameHub : Hub
{
    private readonly GameEngine _engine;
    private readonly IRoundRepository _rounds;
    private readonly IAsyncRoundRepository? _db;
    private readonly IRGSAdapter _rgs;
    private readonly ILogger<GameHub> _logger;

    private readonly IGameRegistry _games;

    public GameHub(GameEngine engine, IRoundRepository rounds, IRGSAdapter rgs, IGameRegistry games, ILogger<GameHub> logger, IAsyncRoundRepository? db = null)
    {
        _engine = engine;
        _games  = games;
        _rounds = rounds;
        _rgs    = rgs;
        _logger = logger;
        _db     = db;
    }

    // Reads player identity from JWT claims — never from client-supplied parameters.
    // 'sub' → playerId, 'name' → username, 'currency' → currency.
    // Fallback covers both mapped (ClaimTypes.*) and raw JWT claim names.
    private (string playerId, string username, string currency) GetPlayerContext()
    {
        var user     = Context.User!;
        var playerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? throw new HubException("Player identity missing from token");
        var username = user.FindFirst(ClaimTypes.Name)?.Value
                    ?? user.FindFirst(JwtRegisteredClaimNames.Name)?.Value
                    ?? "Player";
        var currency = user.FindFirst("currency")?.Value ?? "ZAR";
        return (playerId, username, currency);
    }

    // Which game this connection plays (Feeding Frenzy, Comet Rider, …) — from the token's "game"
    // claim. They share this hub's rounds; the id only decides which title the wallet books under.
    private string? GameId => Context.User?.FindFirst(SessionTokenService.GameClaim)?.Value;

    [EnableRateLimiting("bet")]
    public async Task PlaceBet(decimal amount, int slot, decimal? autoCashOut)
    {
        if (slot < 0 || slot > 1)
        {
            await Clients.Caller.SendAsync("BetError", "Invalid slot index");
            return;
        }
        var (playerId, username, currency) = GetPlayerContext();
        var (success, error) = await _engine.PlaceBetAsync(playerId, username, currency, amount, slot, autoCashOut, GameId);
        if (success)
        {
            var bet = _engine.GetBet(playerId, slot);
            if (bet != null)
                await Clients.All.SendAsync("BetAdded", BetAddedDto.From(bet));

            var balance = await _rgs.GetBalanceAsync(playerId, currency, GameId);
            await Clients.Caller.SendAsync("BetPlaced", new { slot, amount, balance, success = true });
        }
        else
        {
            await Clients.Caller.SendAsync("BetError", error);
        }
    }

    [EnableRateLimiting("bet")]
    public async Task CashOut(int slot)
    {
        if (slot < 0 || slot > 1)
        {
            await Clients.Caller.SendAsync("CashOutError", "Invalid slot index");
            return;
        }
        var (playerId, _, _) = GetPlayerContext();
        var (success, winAmount, error) = await _engine.CashOutAsync(playerId, slot);
        if (success)
        {
            var (_, _, currency) = GetPlayerContext();
            var balance = await _rgs.GetBalanceAsync(playerId, currency, GameId);
            var roundId = _engine.GetBet(playerId, slot)?.RoundId ?? string.Empty;
            await Clients.Caller.SendAsync("CashOutResult", new { slot, winAmount, balance, roundId, success = true });
        }
        else
            await Clients.Caller.SendAsync("CashOutError", error);
    }

    // Not rate-limited under the "bet" policy (5 req/10s) — chasing several
    // fish in quick succession would blow through that. Cheap, low-stakes
    // per-swimmer validation; GameEngine still guards against invalid claims.
    public async Task EatFish(int slot, int fishId)
    {
        if (slot < 0 || slot > 1) return;
        var (playerId, _, _) = GetPlayerContext();
        var (success, boost, _) = _engine.EatFish(playerId, slot, fishId);
        if (success)
            await Clients.Caller.SendAsync("FishEaten", new { slot, fishId, boost });
    }

    public async Task BombHit(int slot, int bombId)
    {
        if (slot < 0 || slot > 1) return;
        var (playerId, _, _) = GetPlayerContext();
        var (success, _) = _engine.BombHit(playerId, slot, bombId);
        if (success)
            await Clients.Caller.SendAsync("PlayerDied", new { slot });
    }

    public async Task GambleWinnings(int slot)
    {
        if (slot < 0 || slot > 1)
        {
            await Clients.Caller.SendAsync("GambleError", "Invalid slot index");
            return;
        }
        var (playerId, _, currency) = GetPlayerContext();
        var (success, won, balance, error) = await _engine.GambleAsync(playerId, slot, currency);
        if (success)
            await Clients.Caller.SendAsync("GambleResult", new { slot, won, balance });
        else
            await Clients.Caller.SendAsync("GambleError", error);
    }

    public async Task GetHistory()
    {
        // Prefer DB (durable, survives restarts) — fall back to in-memory cache.
        var rounds = _db != null ? await _db.GetRecentAsync(20) : _rounds.GetRecent(20);
        await Clients.Caller.SendAsync("History", rounds.Select(HistoryEntryDto.From));
    }

    /// <summary>The calling player's bet history — powers the History view. Identity from JWT only.</summary>
    public async Task GetMyBets()
    {
        var (playerId, _, _) = GetPlayerContext();
        var bets = _db != null ? await _db.GetBetsForPlayerAsync(playerId, 50) : Array.Empty<Domain.Bet>() as IReadOnlyList<Domain.Bet>;
        await Clients.Caller.SendAsync("MyBets", bets.Select(b => new
        {
            b.RoundId,
            b.SlotIndex,
            b.Amount,
            b.Currency,
            b.CashedOutAt,
            b.WinAmount,
            b.PlacedAt,
        }));
    }

    public override async Task OnConnectedAsync()
    {
        // Only games played on this hub may connect — a token for another title (Mystery Box, Margin Call…)
        // must not place bets here that would be booked under that title.
        var gameId = GameId;
        if (gameId != null && gameId != SessionTokenService.DefaultGameId
            && _games.Find(gameId)?.Engine != Domain.EngineKind.Custom)
        {
            _logger.LogWarning("GameHub rejected connection for game {GameId} — not a /gamehub game", gameId);
            Context.Abort();
            return;
        }

        var (playerId, username, currency) = GetPlayerContext();
        decimal balance = 0;
        try { balance = await _rgs.GetBalanceAsync(playerId, currency, GameId); }
        catch (Exception ex) { _logger.LogWarning(ex, "GetBalance failed on connect for {PlayerId} — sending balance=0", playerId); }

        // Tell the client its server-authoritative identity so it can detect its own events.
        await Clients.Caller.SendAsync("ConnectedAs", new { playerId, username, currency, balance });

        var round = _engine.CurrentRound;
        if (round != null)
        {
            var dto = RoundStateDto.From(round, _engine.GetCurrentBets());
            await Clients.Caller.SendAsync("RoundState", dto);
        }
        await base.OnConnectedAsync();
    }
}
