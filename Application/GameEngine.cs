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

    public void UpdateLimits(decimal minBet, decimal maxBet, decimal maxWin)
    {
        _config.MinBet = minBet;
        _config.MaxBet = maxBet;
        _config.MaxWin = maxWin;
    }

    public event Func<Round, Task>? OnRoundStarted;
    public event Func<decimal, Task>? OnMultiplierUpdated;
    public event Func<Round, Task>? OnRoundCrashed;
    public event Func<Bet, Task>? OnBetCashedOut;

    public GameEngine(
        IRNGService rng,
        IRGSAdapter rgs,
        IRoundRepository rounds,
        IConfiguration config,
        ILogger<GameEngine> logger)
    {
        _rng = rng;
        _rgs = rgs;
        _rounds = rounds;
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

            var autoCashouts = new List<Bet>();
            lock (_lock)
            {
                while (_autoCashoutQueue.Count > 0 && _autoCashoutQueue.Keys[0] <= current)
                {
                    autoCashouts.AddRange(_autoCashoutQueue.Values[0]);
                    _autoCashoutQueue.RemoveAt(0);
                }
            }
            foreach (var bet in autoCashouts)
            {
                var pendingBet = bet;
                _ = TryCashOutAsync(pendingBet, (decimal)current).ContinueWith(t =>
                {
                    // A transient wallet failure (timeout, RGS hiccup) must not silently drop the
                    // auto cash-out — re-queue at the same threshold so the next tick retries it,
                    // instead of leaving the player's bet to ride to a possible crash unannounced.
                    if (t.IsCompletedSuccessfully && !t.Result.success)
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

        if (OnRoundCrashed != null) await OnRoundCrashed(round);
    }

    public async Task<(bool success, string error)> PlaceBetAsync(
        string playerId, string username, string currency,
        decimal amount, int slot, decimal? autoCashOut, string? gameId = null)
    {
        Bet bet;
        string roundId;

        lock (_lock)
        {
            if (_currentRound == null || _currentRound.Phase != GamePhase.Waiting)
                return (false, "Betting is closed");
            if (amount <= 0)
                return (false, "Bet amount must be positive");
            if (amount < _config.MinBet || amount > _config.MaxBet)
                return (false, $"Bet must be between {_config.MinBet} and {_config.MaxBet}");
            if (autoCashOut.HasValue && autoCashOut.Value < 1.01m)
                return (false, "Auto cash out must be at least 1.01x");
            if (_currentRound.Bets.Any(b => b.PlayerId == playerId && b.SlotIndex == slot))
                return (false, "Bet already placed for this slot");

            bet = new Bet
            {
                RoundId = _currentRound.RoundId,
                PlayerId = playerId,
                Username = username,
                Currency = currency,
                SlotIndex = slot,
                Amount = amount,
                AutoCashOutAt = autoCashOut,
                GameId = gameId,
            };
            roundId = _currentRound.RoundId;
        }

        try
        {
            var txId = await _rgs.DebitAsync(playerId, amount, currency, roundId, $"BET_{bet.BetId}", bet.GameId);
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

        lock (_lock)
        {
            if (_currentRound == null || _currentRound.Phase != GamePhase.Waiting)
            {
                _ = SafeRollbackAsync(playerId, bet.DebitTransactionId!, roundId, amount, currency, bet.GameId);
                return (false, "Betting closed");
            }
            // Re-check after async debit — two concurrent calls can both pass the first guard.
            if (_currentRound.Bets.Any(b => b.PlayerId == playerId && b.SlotIndex == slot))
            {
                _ = SafeRollbackAsync(playerId, bet.DebitTransactionId!, roundId, amount, currency, bet.GameId);
                return (false, "Bet already placed for this slot");
            }
            _currentRound.Bets.Add(bet);
            if (bet.AutoCashOutAt.HasValue)
                AddToAutoCashoutQueue(bet);
        }

        return (true, string.Empty);
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
            if (bet.IsDead) return (false, 0, "Your fish is dead");

            var flyingStart = _currentRound.FlyingStartedAt ?? _currentRound.StartedAt;
            var elapsed = (DateTime.UtcNow - flyingStart).TotalMilliseconds;
            currentMultiplier = (decimal)Math.Round(MsToMultiplier(elapsed), 2);
        }

        return await TryCashOutAsync(bet, currentMultiplier);
    }

    private async Task<(bool success, decimal winAmount, string error)> TryCashOutAsync(Bet bet, decimal multiplier)
    {
        lock (_lock)
        {
            if (bet.CashedOutAt != null) return (false, 0, "Already cashed out");
            if (bet.IsDead) return (false, 0, "Your fish is dead");
            // Boost earned by eating fish rides along with whatever curve multiplier
            // triggered this cash-out (manual or auto) — reflected in both the stored
            // CashedOutAt (what the client displays) and the payout.
            var effective = multiplier + bet.BoostMultiplier;
            bet.CashedOutAt = effective;
            bet.WinAmount = Math.Round(bet.Amount * effective, 2);
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
            if (ev.Tier >= PlayerTierIndex(MsToMultiplier(elapsed)))
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

    public (bool success, string error) BombHit(string playerId, int slot, int bombId)
    {
        lock (_lock)
        {
            var (bet, elapsed, error) = GetLiveBetForInteraction(playerId, slot);
            if (bet == null) return (false, error);

            if (_spawns == null || !_spawns.TryGetValue(bombId, out var ev)) return (false, "Unknown mine");
            if (!ev.IsBomb) return (false, "Not a mine");
            if (elapsed < ev.TimeMs - 1000 || elapsed > ev.TimeMs + ev.DurationMs + 2500)
                return (false, "Mine not in play");

            bet.IsDead = true;
            return (true, string.Empty);
        }
    }

    // Must be called under _lock.
    private (Bet? bet, double elapsed, string error) GetLiveBetForInteraction(string playerId, int slot)
    {
        if (_currentRound == null || _currentRound.Phase != GamePhase.Flying)
            return (null, 0, "Round is not active");
        var bet = _currentRound.Bets.FirstOrDefault(b => b.PlayerId == playerId && b.SlotIndex == slot);
        if (bet == null) return (null, 0, "No active bet");
        if (bet.CashedOutAt != null) return (null, 0, "Already cashed out");
        if (bet.IsDead) return (null, 0, "Your fish is dead");
        var flyingStart = _currentRound.FlyingStartedAt ?? _currentRound.StartedAt;
        return (bet, (DateTime.UtcNow - flyingStart).TotalMilliseconds, string.Empty);
    }

    /// <summary>Player tier index at a multiplier: 1=clownfish … 5=whale shark (mirrors client fishTiers.ts).</summary>
    private static int PlayerTierIndex(double multiplier) =>
        multiplier >= 25 ? 5 : multiplier >= 10 ? 4 : multiplier >= 5 ? 3 : multiplier >= 2 ? 2 : 1;

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
