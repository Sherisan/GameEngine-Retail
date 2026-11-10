using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

/// <summary>
/// Manages the round state machine: Waiting → Flying → Crashed.
/// Also owns bet placement and cashout because both require the same lock as the round state.
/// History is delegated to IRoundRepository — no persistence concern lives here.
/// </summary>
public class GameEngine
{
    private readonly IRNGService _rng;
    private readonly IRGSAdapter _rgs;
    private readonly IRoundRepository _rounds;
    private readonly IGameConfigStore _gameConfig;
    private readonly IGameRegistry _games;
    private readonly PromoEngine _promos;
    private GameConfig _config;
    private readonly ILogger<GameEngine> _logger;

    private Round? _currentRound;
    private Dictionary<int, SpawnEvent>? _spawns;
    private readonly object _lock = new();
    private long _nonce = 0;

    // Priority queue: threshold → bets at that threshold.
    // O(log n) drain per tick instead of O(n) scan.
    private readonly SortedList<double, List<Bet>> _autoCashoutQueue = new();

    public Round? CurrentRound => _currentRound;
    public int WaitingSeconds => _config.WaitingSeconds;
    public GameConfig Config => _config;

    /// <summary>
    /// Bet limits for a game played on this engine (Feeding Frenzy, Comet Rider): its catalogue entry from the
    /// back office, or the GameConfig section when the game isn't in the catalogue.
    /// </summary>
    public (decimal MinBet, decimal MaxBet, decimal MaxWin) LimitsFor(string? gameId)
    {
        var game = _games.Find(gameId ?? SessionTokenService.DefaultGameId);
        return game != null ? (game.MinBet, game.MaxBet, game.MaxWin) : (_config.MinBet, _config.MaxBet, _config.MaxWin);
    }

    public event Func<Round, Task>? OnRoundStarted;
    public event Func<decimal, Task>? OnMultiplierUpdated;
    public event Func<Round, Task>? OnRoundCrashed;
    public event Func<Bet, Task>? OnBetCashedOut;

    public GameEngine(
        IRNGService rng,
        IRGSAdapter rgs,
        IRoundRepository rounds,
        IGameConfigStore gameConfig,
        IGameRegistry games,
        PromoEngine promos,
        IConfiguration config,
        ILogger<GameEngine> logger)
    {
        _rng = rng;
        _rgs = rgs;
        _rounds = rounds;
        _gameConfig = gameConfig;
        _games = games;
        _promos = promos;
        _logger = logger;
        _config = new GameConfig
        {
            WaitingSeconds = config.GetValue<int>("GameConfig:WaitingSeconds", 5),
            MinBet = config.GetValue<decimal>("GameConfig:MinBet", 1),
            MaxBet = config.GetValue<decimal>("GameConfig:MaxBet", 10000),
            MaxWin = config.GetValue<decimal>("GameConfig:MaxWin", 500000),
        };
    }

    public async Task StartRoundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunWaitingPhaseAsync(ct);
                await RunFlyingPhaseAsync(ct);
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in round loop — broadcasting crash and recovering");
                await TryBroadcastCrashOnErrorAsync();
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task TryBroadcastCrashOnErrorAsync()
    {
        Round? round;
        lock (_lock)
        {
            round = _currentRound;
            if (round != null && round.Phase != GamePhase.Crashed)
            {
                round.Phase = GamePhase.Crashed;
                round.CrashedAt = DateTime.UtcNow;
            }
        }

        if (round == null) return;

        _rounds.Add(round);

        // The round broke rather than crashed: free bets that hadn't cashed out go back to their players.
        List<Bet> unsettled;
        lock (_lock) unsettled = round.Bets.Where(b => b.FreeBet != null && b.CashedOutAt == null).ToList();
        foreach (var bet in unsettled)
            await _promos.ReleaseFreeBetAsync(bet.FreeBet!);

        if (OnRoundCrashed != null)
        {
            try { await OnRoundCrashed(round); }
            catch (Exception ex) { _logger.LogError(ex, "Error broadcasting crash during error recovery"); }
        }
    }

    private async Task RunWaitingPhaseAsync(CancellationToken ct)
    {
        var serverSeed = await _rng.GenerateServerSeedAsync();
        var clientSeed = GenerateClientSeed();
        var crashMultiplier = await _rng.GenerateCrashMultiplierAsync(serverSeed, clientSeed, ++_nonce);

        var round = new Round
        {
            Nonce = _nonce,
            ServerSeed = serverSeed,
            ServerSeedHash = _rng.HashServerSeed(serverSeed),
            ClientSeed = clientSeed,
            CrashMultiplier = crashMultiplier,
            Phase = GamePhase.Waiting,
            StartedAt = DateTime.UtcNow,
        };

        lock (_lock)
        {
            _currentRound = round;
            // Reset queue atomically with the round swap — no orphan auto-cashouts from previous round.
            _autoCashoutQueue.Clear();
            _spawns = null;
        }

        if (OnRoundStarted != null) await OnRoundStarted(round);
        await Task.Delay(_config.WaitingSeconds * 1000, ct);
    }

    private async Task RunFlyingPhaseAsync(CancellationToken ct)
    {
        Round round;
        lock (_lock)
        {
            if (_currentRound == null) return;
            _currentRound.Phase = GamePhase.Flying;
            _currentRound.FlyingStartedAt = DateTime.UtcNow;
            round = _currentRound;
            // Same public seeds the client used for RoundStarted/RoundState — deterministic,
            // reproducible spawn schedule that EatFish/BombHit validate claims against.
            _spawns = SpawnSchedule.Generate(round.ServerSeedHash, round.ClientSeed, round.Nonce);
        }

        var startTime = round.FlyingStartedAt!.Value;
        var targetMs = MultiplierToMs(round.CrashMultiplier);

        while (!ct.IsCancellationRequested)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var current = MsToMultiplier(elapsed);

            // The 100ms tick can land past the crash point on the last pass, so an auto cash-out only triggers for a
            // target the round actually reached, and it pays exactly that target (not the overshot tick value).
            var reached = Math.Min(current, (double)round.CrashMultiplier);
            var autoCashouts = new List<Bet>();
            lock (_lock)
            {
                while (_autoCashoutQueue.Count > 0 && _autoCashoutQueue.Keys[0] <= reached)
                {
                    autoCashouts.AddRange(_autoCashoutQueue.Values[0]);
                    _autoCashoutQueue.RemoveAt(0);
                }
            }
            foreach (var bet in autoCashouts)
            {
                var pendingBet = bet;
                _ = TryCashOutAsync(pendingBet, pendingBet.AutoCashOutAt!.Value).ContinueWith(t =>
                {
                    // A transient wallet failure (timeout, RGS hiccup) must not silently drop the
                    // auto cash-out — re-queue at the same threshold so the next tick retries it,
                    // instead of leaving the player's bet to ride to a possible crash unannounced.
                    // A bet a mine already cashed out is settled, so there is nothing to retry.
                    if (t.IsCompletedSuccessfully && !t.Result.success && pendingBet.CashedOutAt == null)
                    {
                        lock (_lock) { AddToAutoCashoutQueue(pendingBet); }
                        _logger.LogWarning(
                            "Auto cash-out failed for bet {BetId}, re-queued for retry: {Error}",
                            pendingBet.BetId, t.Result.error);
                    }
                }, TaskScheduler.Default);
            }

            if (OnMultiplierUpdated != null) await OnMultiplierUpdated((decimal)Math.Round(current, 2));
            if (elapsed >= targetMs) break;
            await Task.Delay(100, ct);
        }

        lock (_lock)
        {
            round.Phase = GamePhase.Crashed;
            round.CrashedAt = DateTime.UtcNow;
        }

        _rounds.Add(round);
        ReportRoundEnded(round);

        if (OnRoundCrashed != null) await OnRoundCrashed(round);
    }

    public async Task<(bool success, string error)> PlaceBetAsync(
        string playerId, string username, string currency,
        decimal amount, int slot, decimal? autoCashOut, string? gameId = null, bool freeBet = false)
    {
        var promoGame = gameId ?? SessionTokenService.DefaultGameId;

        // A free bet's stake is the promo's, not the player's: claim one first; its value is the bet amount.
        Domain.Promotions.PromoAward? free = null;
        if (freeBet)
        {
            lock (_lock)
                if (_currentRound == null || _currentRound.Phase != GamePhase.Waiting)
                    return (false, "Betting is closed");
            free = await _promos.ClaimFreeBetAsync(new PlayerSession(playerId, username, currency, promoGame), promoGame);
            if (free == null) return (false, "You have no free bet for this game");
            amount = free.Amount;
        }

        var (ok, error, bet, roundId) = Reserve();
        if (!ok)
        {
            if (free != null) await _promos.ReleaseFreeBetAsync(free);
            return (false, error);
        }

        if (free == null)
        {
            try
            {
                var txId = await _rgs.DebitAsync(playerId, amount, currency, roundId, $"BET_{bet!.BetId}", bet.GameId);
                bet.DebitTransactionId = txId;
            }
            catch (RGSException ex)
            {
                _logger.LogWarning("RGS debit failed for {PlayerId}: {Message}", playerId, ex.Message);
                return (false, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RGS debit error for {PlayerId}", playerId);
                return (false, "Wallet error, please try again");
            }
        }

        string? lateError = null;
        lock (_lock)
        {
            if (_currentRound == null || _currentRound.Phase != GamePhase.Waiting)
                lateError = "Betting closed";
            // Re-check after async debit — two concurrent calls can both pass the first guard.
            else if (_currentRound.Bets.Any(b => b.PlayerId == playerId && b.SlotIndex == slot))
                lateError = "Bet already placed for this slot";
            else
            {
                _currentRound.Bets.Add(bet!);
                if (bet!.AutoCashOutAt.HasValue)
                    AddToAutoCashoutQueue(bet);
            }
        }
        if (lateError != null)
        {
            if (free != null) await _promos.ReleaseFreeBetAsync(free);
            else _ = SafeRollbackAsync(playerId, bet!.DebitTransactionId!, roundId, amount, currency, bet.GameId);
            return (false, lateError);
        }

        _promos.BetPlaced(promoGame, playerId, username, currency, amount, freeBet: free != null);
        return (true, string.Empty);

        (bool Ok, string Error, Bet? Bet, string RoundId) Reserve()
        {
            lock (_lock)
            {
                if (_currentRound == null || _currentRound.Phase != GamePhase.Waiting)
                    return (false, "Betting is closed", null, "");
                if (amount <= 0)
                    return (false, "Bet amount must be positive", null, "");
                var (minBet, maxBet, _) = LimitsFor(gameId);
                // Free bets are fixed by the promotion, so the table limits don't apply to them.
                if (free == null && (amount < minBet || amount > maxBet))
                    return (false, $"Bet must be between {minBet} and {maxBet}", null, "");
                if (autoCashOut.HasValue && autoCashOut.Value < 1.01m)
                    return (false, "Auto cash out must be at least 1.01x", null, "");
                if (_currentRound.Bets.Any(b => b.PlayerId == playerId && b.SlotIndex == slot))
                    return (false, "Bet already placed for this slot", null, "");

                return (true, "", new Bet
                {
                    RoundId = _currentRound.RoundId,
                    PlayerId = playerId,
                    Username = username,
                    Currency = currency,
                    SlotIndex = slot,
                    Amount = amount,
                    AutoCashOutAt = autoCashOut,
                    GameId = gameId,
                    FreeBet = free,
                }, _currentRound.RoundId);
            }
        }
    }

    /// <summary>
    /// Tells the promo engine the round is over: free bets still riding at the crash have lost, and each game sharing
    /// this round (Feeding Frenzy, Comet Rider) gets its own round-end report for drops and jackpots.
    /// </summary>
    private void ReportRoundEnded(Round round)
    {
        List<Bet> bets;
        lock (_lock) bets = round.Bets.ToList();

        foreach (var lost in bets.Where(b => b.FreeBet != null && b.CashedOutAt == null))
            _ = _promos.SettleFreeBetAsync(lost.FreeBet!, round.RoundId, 0);

        foreach (var game in bets.GroupBy(b => b.GameId ?? SessionTokenService.DefaultGameId))
            _promos.RoundEnded(game.Key, round.RoundId, round.CrashMultiplier,
                game.Select(b => new PromoBet(b.PlayerId, b.Username, b.Currency, b.Amount, b.FreeBet != null)).ToList());
    }

    public async Task<(bool success, decimal winAmount, string error)> CashOutAsync(string playerId, int slot)
    {
        Bet? bet;
        decimal currentMultiplier;

        lock (_lock)
        {
            if (_currentRound == null || _currentRound.Phase != GamePhase.Flying)
                return (false, 0, "Game is not in flying phase");

            bet = _currentRound.Bets.FirstOrDefault(b => b.PlayerId == playerId && b.SlotIndex == slot);
            if (bet == null) return (false, 0, "No active bet found");
            if (bet.CashedOutAt != null) return (false, 0, "Already cashed out");

            var flyingStart = _currentRound.FlyingStartedAt ?? _currentRound.StartedAt;
            var elapsed = (DateTime.UtcNow - flyingStart).TotalMilliseconds;
            if (PastCrash(_currentRound, elapsed)) return (false, 0, "Round has crashed");
            currentMultiplier = (decimal)Math.Round(MsToMultiplier(elapsed), 2);
        }

        return await TryCashOutAsync(bet, currentMultiplier);
    }

    private async Task<(bool success, decimal winAmount, string error)> TryCashOutAsync(Bet bet, decimal multiplier)
    {
        lock (_lock)
        {
            if (bet.CashedOutAt != null) return (false, 0, "Already cashed out");
            // The fish boost is a percentage on top of whatever curve multiplier triggered this cash-out
            // (manual, auto or mine). Both are floored, so rounding never pays above the curve's RTP.
            var effective = multiplier * (1m + bet.BoostMultiplier);
            bet.CashedOutAt = Math.Floor(effective * 100m) / 100m;
            bet.WinAmount = Math.Floor(bet.Amount * effective * 100m) / 100m;
        }

        if (bet.FreeBet != null)
        {
            // Promo money: the promotion pays what it allows (normally the winnings above the stake) and retries
            // its own payout if the wallet is down, so this cash-out always stands.
            bet.WinAmount = await _promos.SettleFreeBetAsync(bet.FreeBet, bet.RoundId, bet.WinAmount!.Value);
            if (OnBetCashedOut != null) await OnBetCashedOut(bet);
            return (true, bet.WinAmount!.Value, string.Empty);
        }

        try
        {
            var txId = await _rgs.CreditAsync(
                bet.PlayerId, bet.WinAmount!.Value, bet.Currency,
                bet.RoundId, $"WIN_{bet.BetId}", bet.GameId
            );
            bet.CreditTransactionId = txId;
        }
        catch (Exception ex)
        {
            // Revert cashout so a retry is possible; log for manual reconciliation.
            lock (_lock)
            {
                bet.CashedOutAt = null;
                bet.WinAmount = null;
            }
            _logger.LogError(ex, "CRITICAL: RGS credit failed for {PlayerId} win on round {RoundId} — cashout reversed, manual reconciliation required", bet.PlayerId, bet.RoundId);
            return (false, 0, "Wallet credit failed, please try again");
        }

        _promos.BetSettled(bet.GameId ?? SessionTokenService.DefaultGameId, bet.PlayerId, bet.Username, bet.Currency,
            bet.RoundId, bet.Amount, bet.WinAmount!.Value, freeBet: false);
        if (OnBetCashedOut != null) await OnBetCashedOut(bet);
        return (true, bet.WinAmount!.Value, string.Empty);
    }

    private async Task SafeRollbackAsync(string playerId, string txId, string roundId, decimal amount, string currency, string? gameId)
    {
        try
        {
            await _rgs.RollbackAsync(playerId, txId, roundId, gameId, amount, currency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRITICAL: Rollback failed for {PlayerId} tx:{TxId} round:{RoundId} — manual intervention required", playerId, txId, roundId);
        }
    }

    public async Task<(bool success, bool won, decimal balance, string error)> GambleAsync(
        string playerId, int slot, string currency)
    {
        Bet? bet;
        Round? round;
        decimal winAmount;

        lock (_lock)
        {
            // Search current round first, then most recent completed round (auto-cashout may land after crash)
            round = _currentRound != null && _currentRound.Bets.Any(b => b.PlayerId == playerId && b.SlotIndex == slot)
                ? _currentRound
                : _rounds.GetRecent(1).FirstOrDefault();
            bet = round?.Bets.FirstOrDefault(b => b.PlayerId == playerId && b.SlotIndex == slot);

            if (bet == null)                    return (false, false, 0, "No bet found for this slot");
            if (bet.CashedOutAt == null)        return (false, false, 0, "Cash out first before gambling");
            if (bet.HasGambled)                 return (false, false, 0, "Already gambled this round");
            if (bet.FreeBet != null)            return (false, false, 0, "Free bet winnings can't be gambled");
            if (bet.WinAmount == null || bet.WinAmount <= 0) return (false, false, 0, "No winnings to gamble");

            bet.HasGambled = true;
            winAmount = bet.WinAmount.Value;
        }

        // Provably fair: derived from the round's already-revealed seed, same RNG provider as the crash multiplier.
        var won = await _rng.GenerateGambleOutcomeAsync(round!.ServerSeed, round.ClientSeed, round.Nonce, bet.BetId);

        try
        {
            if (won)
                await _rgs.CreditAsync(playerId, winAmount, currency, bet.RoundId, $"GAMBLE_WIN_{bet.BetId}", bet.GameId);
            else
                await _rgs.DebitAsync(playerId, winAmount, currency, bet.RoundId, $"GAMBLE_LOSE_{bet.BetId}", bet.GameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GAMBLE transaction failed for {PlayerId} slot {Slot} — manual reconciliation required", playerId, slot);
        }

        var balance = await _rgs.GetBalanceAsync(playerId, currency, bet.GameId);
        return (true, won, balance, string.Empty);
    }

    // ── Interactive (Feeding Frenzy) mode ────────────────────────────────
    // The client owns fish/player positions and detects collisions locally
    // for responsiveness; the server independently validates every claim
    // against the deterministic schedule — the swimmer must exist, still be
    // alive in its time window, not already eaten, and (for fish) be a tier
    // the player's current size can legitimately eat.

    public (bool success, decimal boost, string error) EatFish(string playerId, int slot, int fishId)
    {
        lock (_lock)
        {
            var (bet, elapsed, error) = GetLiveBetForInteraction(playerId, slot);
            if (bet == null) return (false, 0, error);

            if (_spawns == null || !_spawns.TryGetValue(fishId, out var ev)) return (false, 0, "Unknown fish");
            if (ev.IsBomb) return (false, 0, "That is a mine");
            if (elapsed < ev.TimeMs - 1000 || elapsed > ev.TimeMs + ev.DurationMs + 1500)
                return (false, 0, "Fish not in play");
            if (ev.Tier >= PlayerTierIndex(bet.GameId, MsToMultiplier(elapsed)))
                return (false, 0, "Fish too big to eat");
            if (!bet.EatenFishIds.Add(fishId)) return (false, 0, "Already eaten");
            if (bet.BoostMultiplier >= (decimal)SpawnSchedule.MaxBoost)
                return (false, bet.BoostMultiplier, "Boost cap reached");

            bet.BoostMultiplier = Math.Min(
                (decimal)SpawnSchedule.MaxBoost,
                bet.BoostMultiplier + (decimal)SpawnSchedule.TierBoosts[ev.Tier]);
            return (true, bet.BoostMultiplier, string.Empty);
        }
    }

    /// <summary>
    /// A mine ends the player's fish and cashes the bet out at the current multiplier: the player loses the climb,
    /// not the stake. Cashing out at any point returns the curve's RTP on average, so mines never move the RTP.
    /// </summary>
    public async Task<(bool success, decimal winAmount, string error)> BombHitAsync(string playerId, int slot, int bombId)
    {
        Bet bet;
        decimal multiplier;
        lock (_lock)
        {
            var (live, elapsed, error) = GetLiveBetForInteraction(playerId, slot);
            if (live == null) return (false, 0, error);

            if (_spawns == null || !_spawns.TryGetValue(bombId, out var ev)) return (false, 0, "Unknown mine");
            if (!ev.IsBomb) return (false, 0, "Not a mine");
            if (elapsed < ev.TimeMs - 1000 || elapsed > ev.TimeMs + ev.DurationMs + 2500)
                return (false, 0, "Mine not in play");

            bet = live;
            multiplier = (decimal)Math.Round(MsToMultiplier(elapsed), 2);
        }
        return await TryCashOutAsync(bet, multiplier);
    }

    // Must be called under _lock.
    private (Bet? bet, double elapsed, string error) GetLiveBetForInteraction(string playerId, int slot)
    {
        if (_currentRound == null || _currentRound.Phase != GamePhase.Flying)
            return (null, 0, "Round is not active");
        var bet = _currentRound.Bets.FirstOrDefault(b => b.PlayerId == playerId && b.SlotIndex == slot);
        if (bet == null) return (null, 0, "No active bet");
        if (bet.CashedOutAt != null) return (null, 0, "Already cashed out");
        var flyingStart = _currentRound.FlyingStartedAt ?? _currentRound.StartedAt;
        var elapsed = (DateTime.UtcNow - flyingStart).TotalMilliseconds;
        if (PastCrash(_currentRound, elapsed)) return (null, 0, "Round has crashed");
        return (bet, elapsed, string.Empty);
    }

    /// <summary>
    /// True once the curve has reached the crash point. The flying loop only notices on its next 100ms tick, and
    /// nothing may pay out in that gap, or bets would win above the crash point.
    /// </summary>
    private static bool PastCrash(Round round, double elapsedMs) => elapsedMs >= MultiplierToMs(round.CrashMultiplier);

    /// <summary>
    /// Player tier index at a multiplier: 1=clownfish … 5=whale shark, from the game's evolution ladder set in
    /// the back office (the frontend applies the same ladder in fishTiers.ts / tiers.ts).
    /// </summary>
    private int PlayerTierIndex(string? gameId, double multiplier)
    {
        var tiers = _gameConfig.GetEvolutionTiers(gameId ?? SessionTokenService.DefaultGameId);
        if (tiers.Count == 0) tiers = _gameConfig.GetEvolutionTiers(SessionTokenService.DefaultGameId);
        return EvolutionTiers.LevelAt(tiers, multiplier);
    }

    public List<Bet> GetCurrentBets()
    {
        lock (_lock) { return _currentRound?.Bets.ToList() ?? new(); }
    }

    public Bet? GetBet(string playerId, int slot)
    {
        lock (_lock)
        {
            return _currentRound?.Bets.FirstOrDefault(b => b.PlayerId == playerId && b.SlotIndex == slot);
        }
    }

    // Must be called under _lock.
    private void AddToAutoCashoutQueue(Bet bet)
    {
        var threshold = (double)bet.AutoCashOutAt!.Value;
        if (!_autoCashoutQueue.TryGetValue(threshold, out var list))
        {
            list = new List<Bet>();
            _autoCashoutQueue[threshold] = list;
        }
        list.Add(bet);
    }

    // Math.Exp is a dedicated native instruction — 3-5x faster than Math.Pow(Math.E, x).
    private static double MsToMultiplier(double ms) =>
        Math.Max(1.00, Math.Floor(100 * Math.Exp(0.00006 * ms)) / 100.0);

    private static double MultiplierToMs(decimal multiplier) =>
        Math.Log((double)multiplier) / 0.00006;

    private static string GenerateClientSeed() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLower();
}
