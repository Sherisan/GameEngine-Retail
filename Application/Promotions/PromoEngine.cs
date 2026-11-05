using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Promotions;
using Worldiator.Server.Presentation.Hubs;

namespace Worldiator.Server.Application.Promotions;

/// <summary>A bet in a finished round, as the crash engines report it.</summary>
public record PromoBet(string PlayerId, string Username, string Currency, decimal Stake, bool FreeBet);

/// <summary>
/// Runs the promotions (see Domain/Promotions/Promotion.cs) for the crash games.
///
/// The game engines report what happens — BetPlaced, BetSettled, RoundEnded — without waiting: events go on a
/// queue that one consumer works through in order, so a slow wallet or database never holds up a round, and race
/// places are handed out one at a time. A timer runs rain, leaderboard payouts, retries of failed payouts and
/// free-bet expiry. Free bets are the one synchronous path: an engine claims one when the player bets with it and
/// settles it when the bet ends.
///
/// Money: every cash award is written to the promo ledger first (IPromoStore, idempotent AwardId, budget checked
/// atomically), then credited to the wallet as PROMO_&lt;AwardId&gt; — a failed credit is retried with the same reference.
/// Players hear about it on /promohub (PromoHub): "PromoAward" to the winner, "PromoEvent" to everyone in the game,
/// "PromoChanged" when their free bets change.
///
/// Single-node, like the engines: activity for rain and the queue live in this process.
/// </summary>
public sealed partial class PromoEngine(
    IPromoStore store,
    IRGSAdapter rgs,
    IGameRegistry games,
    IHubContext<PromoHub> hub,
    ILogger<PromoEngine> logger) : BackgroundService
{
    /// <summary>Engines that report to the promo engine. Which promo types suit which engine: Promotion.EnginesFor.</summary>
    public static readonly IReadOnlySet<EngineKind> SupportedEngines = new HashSet<EngineKind>
    {
        EngineKind.Custom, EngineKind.LiveCrash, EngineKind.MarginCall, EngineKind.Instant, EngineKind.Roulette, EngineKind.Blackjack,
    };

    private abstract record PromoEvent;
    private sealed record BetSettledEvent(string GameId, string PlayerId, string Username, string Currency, string RoundId, decimal Stake, decimal Win, DateTime At) : PromoEvent;
    private sealed record RoundEndedEvent(string GameId, string RoundId, decimal CrashPoint, IReadOnlyList<PromoBet> Bets, DateTime At) : PromoEvent;

    private sealed record Activity(string Username, string Currency, decimal Stake, DateTime At);

    private readonly Channel<PromoEvent> _events = Channel.CreateBounded<PromoEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<(string GameId, string PlayerId), Activity> _activity = new();
    private readonly Dictionary<int, DateTime> _nextRain = [];
    private readonly HashSet<(int, string)> _settledPeriods = [];

    public static string GameGroup(string gameId) => $"promo:g:{gameId.ToLowerInvariant()}";
    public static string PlayerGroup(string playerId) => $"promo:p:{playerId}";

    private IEnumerable<Promotion> LiveFor(string gameId, DateTime now) =>
        store.Promotions.Where(p => p.IsLiveAt(now) && p.AppliesTo(gameId));

    // ── Hooks the engines call (never block, never throw) ──────────────────

    /// <summary>A real-money or free bet was accepted.</summary>
    public void BetPlaced(string gameId, string playerId, string username, string currency, decimal stake, bool freeBet)
    {
        // Free bets don't make a player "active" for rain — it rewards players who are wagering.
        if (freeBet) return;
        _activity[(gameId.ToLowerInvariant(), playerId)] = new Activity(username, currency, stake, DateTime.UtcNow);
    }

    /// <summary>A bet paid out (win &gt; 0). Free bets don't count towards leaderboards or races.</summary>
    public void BetSettled(string gameId, string playerId, string username, string currency, string roundId, decimal stake, decimal win, bool freeBet)
    {
        if (freeBet || win <= 0 || stake <= 0) return;
        var now = DateTime.UtcNow;
        if (!LiveFor(gameId, now).Any(p => p.Type is PromoType.MultiplierLeaderboard or PromoType.MultiplierRace or PromoType.Cashback)) return;
        _events.Writer.TryWrite(new BetSettledEvent(gameId, playerId, username, currency, roundId, stake, win, now));
    }

    /// <summary>A round finished. Bets: everyone who had a bet in it (cashed out or not).</summary>
    public void RoundEnded(string gameId, string roundId, decimal crashPoint, IReadOnlyList<PromoBet> bets)
    {
        var now = DateTime.UtcNow;
        if (bets.Count == 0 || !LiveFor(gameId, now).Any(p => p.Type is PromoType.RoundDrop or PromoType.CommunityJackpot
                or PromoType.MultiplierLeaderboard or PromoType.Cashback)) return;
        _events.Writer.TryWrite(new RoundEndedEvent(gameId, roundId, crashPoint, bets, now));
    }

    // ── Free bets (awaited by the engines) ─────────────────────────────────

    /// <summary>Promotions whose free bets the player can use in this game (enabled; free bets outlive the promo window until they expire).</summary>
    private List<int> FreeBetSources(string gameId) => store.Promotions
        .Where(p => p.Enabled && p.AppliesTo(gameId) && p.GivesFreeBets)
        .Select(p => p.PromotionId).ToList();

    /// <summary>Takes one of the player's free bets for a bet in this game, or null when they have none.</summary>
    public async Task<PromoAward?> ClaimFreeBetAsync(PlayerSession player, string gameId)
    {
        try
        {
            var award = await store.ClaimFreeBetAsync(player.PlayerId, FreeBetSources(gameId), DateTime.UtcNow);
            if (award == null) return null;
            award.GameId   = gameId;
            award.Username = player.Username;
            award.Currency = player.Currency;
            await store.UpdateAwardAsync(award);
            await hub.Clients.Group(PlayerGroup(player.PlayerId)).SendAsync("PromoChanged");
            return award;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Free bet claim failed for player {PlayerId}", player.PlayerId);
            return null;
        }
    }

    /// <summary>Roulette: the bet types a free chip may go on (empty = any), from the promotion that gave it.</summary>
    public IReadOnlyList<string> FreeBetTypesFor(PromoAward freeBet) =>
        store.Promotions.FirstOrDefault(p => p.PromotionId == freeBet.PromotionId)?.FreeBet?.BetTypes ?? [];

    /// <summary>The bet using this free bet didn't go through — the player keeps it.</summary>
    public async Task ReleaseFreeBetAsync(PromoAward freeBet)
    {
        try
        {
            freeBet.Status = AwardStatus.Available;
            await store.UpdateAwardAsync(freeBet);
            await hub.Clients.Group(PlayerGroup(freeBet.PlayerId)).SendAsync("PromoChanged");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Free bet {AwardId} could not be released back to player {PlayerId} — it is stuck InPlay", freeBet.AwardId, freeBet.PlayerId);
        }
    }

    /// <summary>
    /// Ends a free bet. win = what the bet returned (0 when it lost). Pays the winnings above the stake (or the
    /// whole return when the promotion says StakeReturned), capped at the game's max win. Returns the cash paid.
    /// </summary>
    public async Task<decimal> SettleFreeBetAsync(PromoAward freeBet, string roundId, decimal win)
    {
        var promo = store.Promotions.FirstOrDefault(p => p.PromotionId == freeBet.PromotionId);
        var game = games.All.FirstOrDefault(g => string.Equals(g.GameId, freeBet.GameId, StringComparison.OrdinalIgnoreCase));
        // A free-bet promotion says what a win pays. Free cards earned in card games (buy-X, mystery prizes, collections)
        // pay the card's whole prize, like a bought card; elsewhere earned free bets pay the winnings above the stake.
        var stakeReturned = promo?.FreeBet?.StakeReturned ?? game?.Engine == EngineKind.Instant;
        var maxWin = game?.MaxWin ?? decimal.MaxValue;
        var payout = Math.Min(maxWin, Math.Round(stakeReturned ? win : Math.Max(0, win - freeBet.Amount), 2, MidpointRounding.ToZero));

        try
        {
            freeBet.Status    = AwardStatus.Used;
            freeBet.RoundId   = roundId;
            freeBet.WinAmount = win;
            freeBet.SettledAt = DateTime.UtcNow;
            await store.UpdateAwardAsync(freeBet);

            if (payout > 0)
            {
                var cash = new PromoAward
                {
                    AwardId = PromoAward.IdFor("freebet-win", freeBet.AwardId), PromotionId = freeBet.PromotionId, GameId = freeBet.GameId,
                    PlayerId = freeBet.PlayerId, Username = freeBet.Username, Currency = freeBet.Currency, Kind = AwardKind.Cash,
                    Amount = payout, Reason = "Free bet win", Status = AwardStatus.Pending, RoundId = roundId, ParentAwardId = freeBet.AwardId,
                };
                // Promised to the player when the free bet was given, so it's paid even if the budget has run out since.
                if (await store.AddAwardAsync(cash, decimal.MaxValue, enforceBudget: false) == AwardAddResult.Added)
                    await PayAsync(cash);
            }
            await hub.Clients.Group(PlayerGroup(freeBet.PlayerId)).SendAsync("PromoChanged");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Free bet {AwardId} settlement failed: player={PlayerId} round={RoundId} win={Win} — reconcile by hand",
                freeBet.AwardId, freeBet.PlayerId, roundId, win);
        }
        return payout;
    }

    // ── Background ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await store.RefreshAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Promotions could not be loaded — retrying in the background"); }

        var consumer = ConsumeAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                tick++;
                await Safe(() => RainAsync(DateTime.UtcNow), "rain");
                if (tick % 3 == 0) await Safe(() => store.RefreshAsync(ct), "refresh");
                if (tick % 12 == 0)
                {
                    await Safe(() => SettleLeaderboardsAsync(DateTime.UtcNow), "leaderboards");
                    await Safe(() => SettleCashbackAsync(DateTime.UtcNow), "cashback");
                    await Safe(RetryFailedAsync, "retry");
                    await Safe(async () => { var n = await store.ExpireFreeBetsAsync(DateTime.UtcNow); if (n > 0) logger.LogInformation("Expired {Count} free bets", n); }, "expiry");
                    PruneActivity(DateTime.UtcNow);
                }
            }
        }
        catch (OperationCanceledException) { }
        await consumer;
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct))
            {
                await Safe(() => e switch
                {
                    BetSettledEvent s => OnBetSettledAsync(s),
                    RoundEndedEvent r => OnRoundEndedAsync(r),
                    _ => Task.CompletedTask,
                }, e.GetType().Name);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Safe(Func<Task> work, string what)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Promotions: {What} failed", what); }
    }

    // ── Leaderboards and races ─────────────────────────────────────────────

    private async Task OnBetSettledAsync(BetSettledEvent e)
    {
        var multiple = Math.Round(e.Win / e.Stake, 2, MidpointRounding.ToZero);
        foreach (var p in LiveFor(e.GameId, e.At).Where(p => e.Stake >= p.MinStake))
        {
            // Stakes were counted when the round ended (RoundEnded), so only the win counts here.
            if (p.Type == PromoType.MultiplierLeaderboard)
                await RecordLeaderboardAsync(p, e.GameId, e.PlayerId, e.Username, e.Currency, e.Stake, e.Win, hits: 0, countStake: false, e.At);
            else if (p.Type == PromoType.Cashback)
                await RecordCashbackAsync(p, e.GameId, e.PlayerId, e.Username, e.Currency, -e.Win, e.At);
            else if (p.Type == PromoType.MultiplierRace && multiple >= p.Race!.TargetMultiplier)
            {
                var place = await store.CountAwardsAsync(p.PromotionId) + 1;
                if (place > p.Race.Winners) continue;
                var award = await AwardCashAsync(p, e.GameId, e.PlayerId, e.Username, e.Currency, p.Race.Prize,
                    Inv($"Race to {p.Race.TargetMultiplier}× — place {place}"), PromoAward.IdFor("race", p.PromotionId, e.PlayerId), e.RoundId);
                if (award != null)
                    await AnnounceAsync(p, e.GameId, Inv($"{Mask(e.Username)} reached {multiple}× and takes place {place} of {p.Race.Winners}!"),
                        p.Race.Prize, [Mask(e.Username)]);
            }
        }
    }

    /// <summary>Pays the periods that have ended. Idempotent: prize award ids are per (promotion, period, rank).</summary>
    private async Task SettleLeaderboardsAsync(DateTime now)
    {
        foreach (var p in store.Promotions.Where(p => p is { Enabled: true, Type: PromoType.MultiplierLeaderboard }))
        {
            var lb = p.Leaderboard!;
            string? key = lb.Period switch
            {
                LeaderboardPeriod.Once => p.EndsAt <= now ? "all" : null,
                LeaderboardPeriod.Daily => PeriodKey(lb.Period, now.AddDays(-1)),
                _ => PeriodKey(lb.Period, now.AddDays(-7)),
            };
            // Marked settled only after the loop, so a failure part-way is retried next time (award ids make that safe).
            if (key == null || _settledPeriods.Contains((p.PromotionId, key))) continue;

            var top = await store.TopScoresAsync(p.PromotionId, key, lb.Prizes.Count);
            var winners = new List<string>();
            for (var i = 0; i < top.Count; i++)
            {
                var s = top[i];
                var award = await AwardCashAsync(p, s.GameId, s.PlayerId, s.Username, s.Currency, lb.Prizes[i],
                    Inv($"Leaderboard {key}: rank {i + 1} ({ScoreText(lb.Metric, s.Score)})"), PromoAward.IdFor("leaderboard", p.PromotionId, key, i + 1), roundId: null);
                if (award != null) winners.Add(Mask(s.Username));
            }
            _settledPeriods.Add((p.PromotionId, key));
            if (winners.Count > 0)
                foreach (var gameId in p.GameIds)
                    await AnnounceAsync(p, gameId, $"Leaderboard results are in — congratulations {string.Join(", ", winners.Take(3))}!",
                        lb.Prizes.Take(winners.Count).Sum(), winners);
        }
    }

    public static string PeriodKey(LeaderboardPeriod period, DateTime utc) => period switch
    {
        LeaderboardPeriod.Daily => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        LeaderboardPeriod.Weekly => Inv($"{ISOWeek.GetYear(utc)}-W{ISOWeek.GetWeekOfYear(utc):00}"),
        _ => "all",
    };

    /// <summary>When the current period ends (UTC), for the countdown players see.</summary>
    public static DateTime? PeriodEnds(Promotion p, DateTime utc) => p.Leaderboard!.Period switch
    {
        LeaderboardPeriod.Daily => utc.Date.AddDays(1),
        LeaderboardPeriod.Weekly => ISOWeek.ToDateTime(ISOWeek.GetYear(utc), ISOWeek.GetWeekOfYear(utc), DayOfWeek.Monday).AddDays(7),
        _ => p.EndsAt,
    };

    // ── Round drops and community jackpots ─────────────────────────────────

    private async Task OnRoundEndedAsync(RoundEndedEvent e)
    {
        foreach (var p in LiveFor(e.GameId, e.At))
        {
            // Real-money bets at or above the min stake, one entry per player.
            var players = e.Bets.Where(b => !b.FreeBet && b.Stake >= p.MinStake)
                .GroupBy(b => b.PlayerId)
                .Select(g => (Bet: g.First(), Stake: g.Sum(b => b.Stake)))
                .ToList();
            if (players.Count == 0) continue;

            if (p.Type == PromoType.MultiplierLeaderboard)
            {
                foreach (var (bet, stake) in players)
                    await RecordLeaderboardAsync(p, e.GameId, bet.PlayerId, bet.Username, bet.Currency, stake, win: 0, hits: 0, countStake: true, e.At);
            }
            else if (p.Type == PromoType.Cashback)
            {
                // Wins come off as they're paid (BetSettled), so the net loss builds up round by round.
                foreach (var (bet, stake) in players)
                    await RecordCashbackAsync(p, e.GameId, bet.PlayerId, bet.Username, bet.Currency, stake, e.At);
            }
            else if (p.Type == PromoType.RoundDrop && RandomUnit() < p.Drop!.Chance)
            {
                var winners = new List<string>();
                foreach (var (bet, _) in Shuffle(players).Take(p.Drop.Winners))
                {
                    var id = PromoAward.IdFor("drop", p.PromotionId, e.GameId, e.RoundId, bet.PlayerId);
                    var ok = p.Drop.Prize == DropPrize.Cash
                        ? await AwardCashAsync(p, e.GameId, bet.PlayerId, bet.Username, bet.Currency, p.Drop.Amount, $"Round drop (round {e.RoundId})", id, e.RoundId) != null
                        : await AwardFreeBetAsync(p, e.GameId, bet.PlayerId, bet.Username, bet.Currency, p.Drop.Amount, p.Drop.FreeBetValidDays, $"Round drop (round {e.RoundId})", id, e.RoundId) != null;
                    if (ok) winners.Add(Mask(bet.Username));
                }
                if (winners.Count > 0)
                    await AnnounceAsync(p, e.GameId,
                        $"Round drop! {string.Join(", ", winners)} {(winners.Count == 1 ? "gets" : "each get")} " +
                        (p.Drop.Prize == DropPrize.Cash ? Inv($"{p.Drop.Amount:0.00}") : Inv($"a {p.Drop.Amount:0.00} free bet")) + ".",
                        p.Drop.Amount * winners.Count, winners);
            }
            else if (p.Type == PromoType.CommunityJackpot && e.CrashPoint >= p.Jackpot!.TriggerMultiplier)
            {
                var j = p.Jackpot;
                if (await store.CountRoundsAsync(p.PromotionId, e.At.Date) >= j.MaxHitsPerDay) continue;

                var total = players.Sum(x => x.Stake);
                var shares = players.Select(x => (x.Bet, Share: Math.Round(
                    j.SplitByStake ? j.Pool * x.Stake / total : j.Pool / players.Count, 2, MidpointRounding.ToZero))).Where(x => x.Share > 0).ToList();
                var paid = 0m;
                foreach (var (bet, share) in shares)
                {
                    var award = await AwardCashAsync(p, e.GameId, bet.PlayerId, bet.Username, bet.Currency, share,
                        Inv($"Community jackpot — crash at {e.CrashPoint}× (round {e.RoundId})"),
                        PromoAward.IdFor("jackpot", p.PromotionId, e.GameId, e.RoundId, bet.PlayerId), e.RoundId);
                    if (award != null) paid += share;
                }
                if (paid > 0)
                    await AnnounceAsync(p, e.GameId,
                        Inv($"JACKPOT! The round crashed at {e.CrashPoint}× — {paid:0.00} shared between {shares.Count} player{(shares.Count == 1 ? "" : "s")}."),
                        paid, shares.Select(s => Mask(s.Bet.Username)).ToList());
            }
        }
    }

    // ── Rain ────────────────────────────────────────────────────────────────

    private async Task RainAsync(DateTime now)
    {
        var live = store.Promotions.Where(p => p.Type == PromoType.Rain && p.IsLiveAt(now)).ToList();
        foreach (var id in _nextRain.Keys.Where(id => live.All(p => p.PromotionId != id)).ToList()) _nextRain.Remove(id);

        foreach (var p in live)
        {
            var r = p.Rain!;
            if (!_nextRain.TryGetValue(p.PromotionId, out var at))
            {
                _nextRain[p.PromotionId] = now.AddMinutes(r.MinIntervalMinutes + RandomUnit() * (r.MaxIntervalMinutes - r.MinIntervalMinutes));
                continue;
            }
            if (now < at) continue;
            _nextRain[p.PromotionId] = now.AddMinutes(r.MinIntervalMinutes + RandomUnit() * (r.MaxIntervalMinutes - r.MinIntervalMinutes));
            await RainNowAsync(p, now);
        }
    }

    /// <summary>Lets rain fall now (also used by the back office "Rain now" button). Returns how many players it reached.</summary>
    public async Task<int> RainNowAsync(Promotion p, DateTime now)
    {
        var r = p.Rain!;
        var since = now.AddMinutes(-r.ActiveWithinMinutes);
        var active = _activity
            .Where(kv => p.AppliesTo(kv.Key.GameId) && kv.Value.At >= since && kv.Value.Stake >= p.MinStake)
            .GroupBy(kv => kv.Key.PlayerId).Select(g => g.OrderByDescending(kv => kv.Value.At).First())
            .ToList();
        if (active.Count == 0)
        {
            logger.LogInformation("Rain {PromotionId}: nobody active — skipped", p.PromotionId);
            return 0;
        }

        var picked = Shuffle(active).Take(r.Recipients).ToList();
        var share = Math.Round(r.Pool / picked.Count, 2, MidpointRounding.ToZero);
        var stamp = now.Ticks;
        var names = new List<string>();
        foreach (var kv in picked)
        {
            var award = await AwardCashAsync(p, kv.Key.GameId, kv.Key.PlayerId, kv.Value.Username, kv.Value.Currency, share,
                "Rain", PromoAward.IdFor("rain", p.PromotionId, stamp, kv.Key.PlayerId), roundId: null);
            if (award != null) names.Add(Mask(kv.Value.Username));
        }
        if (names.Count > 0)
            foreach (var gameId in p.GameIds)
                await AnnounceAsync(p, gameId, Inv($"It's raining! {share:0.00} each for {names.Count} active player{(names.Count == 1 ? "" : "s")}."),
                    share * names.Count, names);
        return names.Count;
    }

    private void PruneActivity(DateTime now)
    {
        foreach (var key in _activity.Where(kv => kv.Value.At < now.AddHours(-2)).Select(kv => kv.Key).ToList())
            _activity.TryRemove(key, out _);
    }

    // ── Awards and payouts ──────────────────────────────────────────────────

    private async Task<PromoAward?> AwardCashAsync(Promotion p, string gameId, string playerId, string username, string currency,
        decimal amount, string reason, string awardId, string? roundId)
    {
        var award = new PromoAward
        {
            AwardId = awardId, PromotionId = p.PromotionId, GameId = gameId, PlayerId = playerId, Username = username, Currency = currency,
            Kind = AwardKind.Cash, Amount = amount, Reason = reason, Status = AwardStatus.Pending, RoundId = roundId,
        };
        var added = await store.AddAwardAsync(award, p.Budget, enforceBudget: true);
        if (added == AwardAddResult.OverBudget)
            logger.LogWarning("Promotion {PromotionId} \"{Name}\" is out of budget — {Amount} to {PlayerId} not paid ({Reason})",
                p.PromotionId, p.Name, amount, playerId, reason);
        if (added != AwardAddResult.Added) return null;

        await PayAsync(award);
        // The game shows the new balance straight away — its own hub only sends one with bets and cash-outs.
        decimal? balance = null;
        if (award.Status == AwardStatus.Paid)
        {
            try { balance = await rgs.GetBalanceAsync(playerId, currency, string.IsNullOrEmpty(gameId) ? null : gameId); }
            catch (Exception ex) { logger.LogDebug(ex, "Balance after promo payout unavailable for {PlayerId}", playerId); }
        }
        await hub.Clients.Group(PlayerGroup(playerId)).SendAsync("PromoAward", AwardDto(award, p, balance));
        return award;
    }

    /// <summary>Gives a free bet (counted against the budget at its stake).</summary>
    public async Task<PromoAward?> AwardFreeBetAsync(Promotion p, string gameId, string playerId, string username, string currency,
        decimal stake, int validDays, string reason, string awardId, string? roundId)
    {
        var award = new PromoAward
        {
            AwardId = awardId, PromotionId = p.PromotionId, GameId = gameId, PlayerId = playerId, Username = username, Currency = currency,
            Kind = AwardKind.FreeBet, Amount = stake, Reason = reason, Status = AwardStatus.Available, RoundId = roundId,
            ExpiresAt = DateTime.UtcNow.AddDays(validDays),
        };
        var added = await store.AddAwardAsync(award, p.Budget, enforceBudget: true);
        if (added != AwardAddResult.Added) return null;
        await hub.Clients.Group(PlayerGroup(playerId)).SendAsync("PromoAward", AwardDto(award, p));
        await hub.Clients.Group(PlayerGroup(playerId)).SendAsync("PromoChanged");
        return award;
    }

    /// <summary>Credits a cash award to the wallet. A failure leaves it Failed for RetryFailedAsync (same reference, so never paid twice).</summary>
    private async Task PayAsync(PromoAward award)
    {
        award.Attempts++;
        try
        {
            var walletGame = string.IsNullOrEmpty(award.GameId) ? null : award.GameId;
            await rgs.CreditAsync(award.PlayerId, award.Amount, award.Currency, $"{award.GameId}:promo-{award.PromotionId}",
                $"PROMO_{award.AwardId}", walletGame);
            award.Status    = AwardStatus.Paid;
            award.SettledAt = DateTime.UtcNow;
            award.Error     = null;
            logger.LogInformation("Promo paid: promotion={PromotionId} player={PlayerId} amount={Amount} award={AwardId} ({Reason})",
                award.PromotionId, award.PlayerId, award.Amount, award.AwardId, award.Reason);
        }
        catch (Exception ex)
        {
            award.Status = AwardStatus.Failed;
            award.Error  = ex.Message;
            logger.LogError(ex, "Promo payout failed (will retry): promotion={PromotionId} player={PlayerId} amount={Amount} award={AwardId}",
                award.PromotionId, award.PlayerId, award.Amount, award.AwardId);
        }
        await store.UpdateAwardAsync(award);
    }

    private async Task RetryFailedAsync()
    {
        foreach (var award in await store.ListAwardsAsync(null, null, AwardStatus.Failed, 100))
        {
            if (award.Attempts >= 50) continue; // left for someone to look at in the back office
            await PayAsync(award);
            if (award.Status == AwardStatus.Paid)
                await hub.Clients.Group(PlayerGroup(award.PlayerId)).SendAsync("PromoChanged");
        }
    }

    // ── Player-facing state (PromoHub.GetState) ────────────────────────────

    public async Task<object> StateForAsync(PlayerSession player)
    {
        var now = DateTime.UtcNow;
        var gameId = player.GameId;
        var live = LiveFor(gameId, now).ToList();

        // "Free bets on first visit": granted the first time the player opens a game in the promotion.
        foreach (var p in live.Where(p => p is { Type: PromoType.FreeBet, FreeBet.Grant: FreeBetGrant.OnFirstVisit }))
            for (var i = 1; i <= p.FreeBet!.BetsPerPlayer; i++)
                if (await AwardFreeBetAsync(p, gameId, player.PlayerId, player.Username, player.Currency, p.FreeBet.Stake, p.FreeBet.ValidDays,
                        "Welcome free bet", PromoAward.IdFor("visit", p.PromotionId, player.PlayerId, i), roundId: null) == null
                    && await store.GetAwardAsync(PromoAward.IdFor("visit", p.PromotionId, player.PlayerId, i)) == null)
                    break; // out of budget

        var sources = FreeBetSources(gameId);
        var freeBets = (await store.ListAwardsAsync(null, player.PlayerId, AwardStatus.Available, 100))
            .Where(a => a.Kind == AwardKind.FreeBet && sources.Contains(a.PromotionId) && (a.ExpiresAt == null || a.ExpiresAt > now))
            .OrderBy(a => a.ExpiresAt)
            .Select(a =>
            {
                var source = store.Promotions.FirstOrDefault(p => p.PromotionId == a.PromotionId);
                // Roulette free chips may be limited to some bet types.
                return new { a.AwardId, a.Amount, a.ExpiresAt, promotion = source?.Name, betTypes = source?.FreeBet?.BetTypes ?? [] };
            })
            .ToList();

        // What the player won lately — games without the hub (cards, roulette, blackjack) show these as they appear.
        var recentAwards = (await store.ListAwardsAsync(null, player.PlayerId, null, 20))
            .Where(a => a.CreatedAt > now.AddDays(-7) && a.Status is not (AwardStatus.Failed or AwardStatus.Cancelled))
            .Select(a => new
            {
                a.AwardId, kind = a.Kind.ToString(), a.Amount, a.Reason, a.CreatedAt, a.GameId, a.RoundId,
                promotion = store.Promotions.FirstOrDefault(p => p.PromotionId == a.PromotionId)?.Name,
            })
            .ToList();

        var promos = new List<object>();
        foreach (var p in live)
            promos.Add(await PublicPromoAsync(p, player.PlayerId, now));

        return new { serverTime = now, promotions = promos, freeBets, recentAwards };
    }

    private async Task<object> PublicPromoAsync(Promotion p, string playerId, DateTime now)
    {
        object? details = p.Type switch
        {
            PromoType.FreeBet => new { p.FreeBet!.Stake, p.FreeBet.StakeReturned, p.FreeBet.BetTypes },
            PromoType.Rain => new { p.Rain!.Pool, p.Rain.Recipients, p.Rain.ActiveWithinMinutes },
            PromoType.RoundDrop => new { p.Drop!.Amount, prize = p.Drop.Prize.ToString(), p.Drop.Chance, p.Drop.Winners },
            PromoType.MultiplierRace => new
            {
                p.Race!.TargetMultiplier, p.Race.Prize, p.Race.Winners,
                remaining = Math.Max(0, p.Race.Winners - await store.CountAwardsAsync(p.PromotionId)),
            },
            PromoType.CommunityJackpot => new { p.Jackpot!.TriggerMultiplier, p.Jackpot.Pool, p.Jackpot.SplitByStake },
            PromoType.MultiplierLeaderboard => await LeaderboardAsync(p, playerId, now),
            _ => await TableDetailsAsync(p, playerId, now),
        };
        return new
        {
            p.PromotionId, p.Name, type = p.Type.ToString(), p.Terms, p.MinStake, p.EndsAt, details,
        };
    }

    private async Task<object> LeaderboardAsync(Promotion p, string playerId, DateTime now)
    {
        var key = PeriodKey(p.Leaderboard!.Period, now);
        var top = await store.TopScoresAsync(p.PromotionId, key, 100);
        var mine = top.Select((s, i) => (s, rank: i + 1)).FirstOrDefault(x => x.s.PlayerId == playerId);
        return new
        {
            period = p.Leaderboard.Period.ToString(), metric = p.Leaderboard.Metric.ToString(), periodKey = key, periodEndsAt = PeriodEnds(p, now), p.Leaderboard.Prizes,
            top = top.Take(10).Select((s, i) => new { rank = i + 1, player = Mask(s.Username), s.Score, s.GameId }),
            me = mine.s == null ? null : new { mine.rank, mine.s.Score },
        };
    }

    private Task AnnounceAsync(Promotion p, string gameId, string message, decimal amount, IReadOnlyList<string> winners) =>
        hub.Clients.Group(GameGroup(gameId)).SendAsync("PromoEvent", new
        {
            p.PromotionId, p.Name, type = p.Type.ToString(), message, amount, winners = winners.Take(20), at = DateTime.UtcNow,
        });

    private static object AwardDto(PromoAward a, Promotion p, decimal? balance = null) => new
    {
        a.AwardId, a.PromotionId, promotion = p.Name, type = p.Type.ToString(), kind = a.Kind.ToString(), a.Amount, a.Reason,
        status = a.Status.ToString(), a.ExpiresAt, balance,
    };

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>"dev_1234" → "de***34" — winners are shown to everyone in the game.</summary>
    public static string Mask(string name) =>
        string.IsNullOrEmpty(name) ? "***" : name.Length <= 4 ? name[0] + "***" : name[..2] + "***" + name[^2..];

    /// <summary>Uniform [0, 1) from the OS CSPRNG — prize draws must not be predictable.</summary>
    private static double RandomUnit() => (RandomNumberGenerator.GetInt32(int.MaxValue)) / (double)int.MaxValue;

    private static List<T> Shuffle<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
