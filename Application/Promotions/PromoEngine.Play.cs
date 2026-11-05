using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Promotions;

namespace Worldiator.Server.Application.Promotions;

/// <summary>
/// One finished play of a single-player game (a card, a spin, a blackjack round), as PlayService, RouletteService and
/// BlackjackService report it. Stake / Win are real money only — free-bet stakes and promo payouts are left out.
/// </summary>
public record PlayReport(string GameId, PlayerSession Player, string RoundId, decimal Stake, decimal Win)
{
    /// <summary>The game's special results this play: a roulette straight-up win, a blackjack, a card's top prize.</summary>
    public int Hits { get; init; }
    /// <summary>Roulette: the winning number and the straight-up stake on each number.</summary>
    public int? Number { get; init; }
    public IReadOnlyDictionary<int, decimal> StraightStakes { get; init; } = new Dictionary<int, decimal>();
    /// <summary>Blackjack: the special hands the player made.</summary>
    public IReadOnlySet<SpecialHand> Hands { get; init; } = new HashSet<SpecialHand>();
}

/// <summary>What a play earned from promotions — returned with the play so the game can show it straight away.</summary>
public record PromoOutcome(int PromotionId, string Promotion, string Type, string Kind, decimal Amount, string Message, object? Progress = null);

public sealed partial class PromoEngine
{
    /// <summary>
    /// Runs every live promotion of the game against one play and returns what it earned (cash and free bets are already
    /// paid / granted; "Progress" outcomes carry streak, collection and buy-X progress). Never throws.
    /// Plays that were only free bets (Stake 0) don't count.
    /// </summary>
    public async Task<IReadOnlyList<PromoOutcome>> PlayedAsync(PlayReport r)
    {
        var outcomes = new List<PromoOutcome>();
        if (r.Stake <= 0) return outcomes;
        var now = DateTime.UtcNow;
        var pl = r.Player;
        BetPlaced(r.GameId, pl.PlayerId, pl.Username, pl.Currency, r.Stake, freeBet: false);

        foreach (var p in LiveFor(r.GameId, now).Where(p => r.Stake >= p.MinStake || p.Type == PromoType.LuckyNumber))
        {
            try
            {
                var outcome = await PlayedAsync(p, r, now);
                if (outcome != null) outcomes.Add(outcome);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Promotion {PromotionId} failed for play {RoundId} — the play stands, the promotion skipped it", p.PromotionId, r.RoundId);
            }
        }
        return outcomes;
    }

    private async Task<PromoOutcome?> PlayedAsync(Promotion p, PlayReport r, DateTime now)
    {
        var pl = r.Player;
        PromoOutcome Out(string kind, decimal amount, string message, object? progress = null) =>
            new(p.PromotionId, p.Name, p.Type.ToString(), kind, amount, message, progress);

        switch (p.Type)
        {
            case PromoType.MultiplierLeaderboard:
                await RecordLeaderboardAsync(p, r.GameId, pl.PlayerId, pl.Username, pl.Currency, r.Stake, r.Win, r.Hits, countStake: true, now);
                return null;

            case PromoType.Cashback:
                await RecordCashbackAsync(p, r.GameId, pl.PlayerId, pl.Username, pl.Currency, r.Stake - r.Win, now);
                return null;

            case PromoType.MultiplierRace:
                var multiple = Math.Round(r.Win / r.Stake, 2, MidpointRounding.ToZero);
                if (multiple < p.Race!.TargetMultiplier) return null;
                var place = await store.CountAwardsAsync(p.PromotionId) + 1;
                if (place > p.Race.Winners) return null;
                return await CashAsync(p, r, p.Race.Prize, Inv($"Race to {p.Race.TargetMultiplier}× — place {place}"),
                    PromoAward.IdFor("race", p.PromotionId, pl.PlayerId), Inv($"You took place {place} in the race to {p.Race.TargetMultiplier}×!"));

            case PromoType.MysteryPrize:
                var m = p.Mystery!;
                if (RandomUnit() >= m.Chance) return null;
                return await PrizeAsync(p, r, m.Prize, m.Amount, m.FreeBetValidDays, "Mystery prize",
                    PromoAward.IdFor("mystery", p.PromotionId, r.RoundId), "Mystery prize!");

            case PromoType.BuyXGetFree:
            {
                var b = p.BuyX!;
                var progress = await store.GetProgressAsync(p.PromotionId, pl.PlayerId);
                progress.Count++;
                progress.LastAt = now;
                PromoOutcome result;
                if (progress.Count >= b.Buy && progress.TodayCount(now) < b.MaxFreePerDay)
                {
                    progress.Count = 0;
                    progress.Completed++;
                    progress.CountToday(now);
                    var free = await AwardFreeBetAsync(p, r.GameId, pl.PlayerId, pl.Username, pl.Currency, r.Stake, b.FreeBetValidDays,
                        Inv($"Buy {b.Buy}, get one free"), PromoAward.IdFor("buyx", p.PromotionId, pl.PlayerId, progress.Completed), r.RoundId);
                    result = free != null
                        ? Out("FreeBet", r.Stake, Inv($"You've earned a free card worth {r.Stake:0.00}!"), new { count = 0, buy = b.Buy })
                        : Out("Progress", 0, "This offer has run out.", new { count = 0, buy = b.Buy });
                }
                else
                {
                    // At the daily cap the count stays full, so the next card after midnight earns the free one.
                    progress.Count = Math.Min(progress.Count, b.Buy);
                    result = Out("Progress", 0, Inv($"{progress.Count} of {b.Buy} towards a free card"), new { count = progress.Count, buy = b.Buy });
                }
                await store.SaveProgressAsync(progress);
                return result;
            }

            case PromoType.CollectionMission:
            {
                var c = p.Collection!;
                var progress = await store.GetProgressAsync(p.PromotionId, pl.PlayerId);
                if (c.MaxCompletions > 0 && progress.Completed >= c.MaxCompletions) return null;
                if (RandomUnit() >= c.ChancePerCard) return null;
                var symbol = c.Symbols[RandomNumberGenerator.GetInt32(c.Symbols.Count)];
                var have = progress.Data.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isNew = have.Add(symbol);
                progress.LastAt = now;
                PromoOutcome result;
                if (c.Symbols.All(have.Contains))
                {
                    progress.Completed++;
                    progress.Data = string.Empty;
                    var prize = await PrizeAsync(p, r, c.Prize, c.Reward, c.FreeBetValidDays, "Collection complete",
                        PromoAward.IdFor("collection", p.PromotionId, pl.PlayerId, progress.Completed), $"You collected the whole set!");
                    result = (prize ?? Out("Progress", 0, "Set complete — but this offer has run out."))
                        with { Progress = new { symbol, isNew, collected = c.Symbols, symbols = c.Symbols, complete = true } };
                }
                else
                {
                    progress.Data = string.Join(',', c.Symbols.Where(have.Contains));
                    result = Out("Progress", 0, isNew ? $"You found a {symbol}! ({have.Count} of {c.Symbols.Count})" : $"Another {symbol} — you have it already.",
                        new { symbol, isNew, collected = c.Symbols.Where(have.Contains).ToList(), symbols = c.Symbols, complete = false });
                }
                await store.SaveProgressAsync(progress);
                return result;
            }

            case PromoType.StreakMission:
            {
                var s = p.Streak!;
                var progress = await store.GetProgressAsync(p.PromotionId, pl.PlayerId);
                if (progress.LastAt is { } last && now - last > TimeSpan.FromMinutes(s.MaxGapMinutes)) progress.Streak = 0;
                progress.LastAt = now;
                if (s.Counts == StreakCounts.Plays) progress.Streak++;
                else if (r.Win > r.Stake) progress.Streak++;
                else if (r.Win < r.Stake) progress.Streak = 0; // a push leaves a win streak where it was

                PromoOutcome? result = null;
                if (progress.Streak >= s.Target)
                {
                    progress.Streak = 0;
                    if (progress.TodayCount(now) < s.MaxRewardsPerDay)
                    {
                        progress.Completed++;
                        progress.CountToday(now);
                        result = await PrizeAsync(p, r, s.Prize, s.Reward, s.FreeBetValidDays,
                            Inv($"{s.Target} {(s.Counts == StreakCounts.Wins ? "wins" : "plays")} in a row"),
                            PromoAward.IdFor("streak", p.PromotionId, pl.PlayerId, progress.Completed),
                            Inv($"Streak complete — {s.Target} {(s.Counts == StreakCounts.Wins ? "wins" : "in a row")}!"));
                    }
                }
                await store.SaveProgressAsync(progress);
                return (result ?? Out("Progress", 0, Inv($"Streak {progress.Streak} of {s.Target}")))
                    with { Progress = new { streak = progress.Streak, target = s.Target, counts = s.Counts.ToString() } };
            }

            case PromoType.LuckyNumber:
            {
                var l = p.Lucky!;
                var lucky = LuckyNumberFor(p, now);
                if (r.Number != lucky || r.StraightStakes.GetValueOrDefault(lucky) < Math.Max(0.01m, p.MinStake)) return null;
                var progress = await store.GetProgressAsync(p.PromotionId, pl.PlayerId);
                if (progress.TodayCount(now) >= l.MaxPrizesPerPlayerPerDay) return null;
                var prize = await CashAsync(p, r, l.Prize, Inv($"Lucky number {lucky}"), PromoAward.IdFor("lucky", p.PromotionId, r.RoundId),
                    Inv($"Lucky number {lucky} hit!"));
                if (prize != null)
                {
                    progress.CountToday(now);
                    progress.LastAt = now;
                    await store.SaveProgressAsync(progress);
                }
                return prize;
            }

            case PromoType.BoostedPayout:
            {
                var b = p.Boost!;
                if (r.Number is not { } n || !b.Numbers.Contains(n)) return null;
                var onNumber = Math.Min(r.StraightStakes.GetValueOrDefault(n), b.MaxBoostedStake);
                if (onNumber <= 0) return null;
                var extra = Math.Round(onNumber * (b.Payout - 35), 2, MidpointRounding.ToZero);
                return await CashAsync(p, r, extra, Inv($"Boosted {n}: {b.Payout} to 1"), PromoAward.IdFor("boost", p.PromotionId, r.RoundId),
                    Inv($"Boosted payout on {n} — {b.Payout} to 1!"));
            }

            case PromoType.HandBonus:
            {
                var h = p.HandBonus!;
                if (!r.Hands.Contains(h.Hand)) return null;
                var progress = await store.GetProgressAsync(p.PromotionId, pl.PlayerId);
                if (progress.TodayCount(now) >= h.MaxPerPlayerPerDay) return null;
                var prize = await CashAsync(p, r, h.Prize, $"{HandName(h.Hand)} bonus", PromoAward.IdFor("hand", p.PromotionId, r.RoundId),
                    $"{HandName(h.Hand)}! Bonus paid.");
                if (prize != null)
                {
                    progress.CountToday(now);
                    progress.LastAt = now;
                    await store.SaveProgressAsync(progress);
                }
                return prize;
            }

            case PromoType.HandJackpot:
            {
                var j = p.HandJackpot!;
                if (!r.Hands.Contains(j.Hand)) return null;
                var left = j.Pool - await store.SumCashAsync(p.PromotionId, now.Date);
                var share = Math.Round(left * j.SharePercent / 100m, 2, MidpointRounding.ToZero);
                if (share < 0.01m) return null;
                var prize = await CashAsync(p, r, share, $"{HandName(j.Hand)} jackpot", PromoAward.IdFor("handjackpot", p.PromotionId, r.RoundId),
                    Inv($"{HandName(j.Hand)} — you win {j.SharePercent:0.##}% of the jackpot!"));
                if (prize != null)
                    await AnnounceAsync(p, r.GameId, Inv($"{Mask(pl.Username)} hit {HandName(j.Hand)} and won {share:0.00} from the jackpot!"), share, [Mask(pl.Username)]);
                return prize;
            }
        }
        return null;
    }

    private async Task<PromoOutcome?> CashAsync(Promotion p, PlayReport r, decimal amount, string reason, string awardId, string message)
    {
        var award = await AwardCashAsync(p, r.GameId, r.Player.PlayerId, r.Player.Username, r.Player.Currency, amount, reason, awardId, r.RoundId);
        return award == null ? null : new PromoOutcome(p.PromotionId, p.Name, p.Type.ToString(), "Cash", amount, message);
    }

    private async Task<PromoOutcome?> PrizeAsync(Promotion p, PlayReport r, DropPrize kind, decimal amount, int validDays, string reason, string awardId, string message)
    {
        if (kind == DropPrize.Cash) return await CashAsync(p, r, amount, reason, awardId, message);
        var award = await AwardFreeBetAsync(p, r.GameId, r.Player.PlayerId, r.Player.Username, r.Player.Currency, amount, validDays, reason, awardId, r.RoundId);
        return award == null ? null : new PromoOutcome(p.PromotionId, p.Name, p.Type.ToString(), "FreeBet", amount, message);
    }

    public static string HandName(SpecialHand h) => h switch
    {
        SpecialHand.SuitedBlackjack => "Suited blackjack",
        SpecialHand.SuitedSevens => "Suited 7s",
        SpecialHand.ThreeSevens => "Three 7s",
        SpecialHand.PerfectPair => "Perfect pair",
        SpecialHand.FiveCardCharlie => "Five-card Charlie",
        _ => "Blackjack",
    };

    /// <summary>The lucky number today: the fixed one, or one drawn per promotion per UTC day (the same for everyone).</summary>
    public static int LuckyNumberFor(Promotion p, DateTime utc)
    {
        if (p.Lucky?.Number is { } fixedNumber) return fixedNumber;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"lucky|{p.PromotionId}|{utc:yyyy-MM-dd}"));
        return (int)(BitConverter.ToUInt32(hash, 0) % 37);
    }

    // ── Leaderboards and cashback (shared with the crash events) ───────────

    /// <summary>
    /// Records a result on a leaderboard. "Best" metrics keep the player's best; "Total" metrics add up. countStake: the
    /// stake should count towards wagered / rounds played (the crash engines report stakes and wins separately).
    /// </summary>
    private async Task RecordLeaderboardAsync(Promotion p, string gameId, string playerId, string username, string currency,
        decimal stake, decimal win, int hits, bool countStake, DateTime at)
    {
        var lb = p.Leaderboard!;
        PromoScore Row(decimal score, decimal rowStake) => new()
        {
            PromotionId = p.PromotionId, PeriodKey = PeriodKey(lb.Period, at), PlayerId = playerId, Username = username,
            Currency = currency, GameId = gameId, Score = score, Stake = rowStake, AchievedAt = at,
        };
        switch (lb.Metric)
        {
            case LeaderboardMetric.BestMultiple when stake > 0 && win > 0:
                await store.UpsertScoreAsync(Row(Math.Round(win / stake, 2, MidpointRounding.ToZero), stake));
                break;
            case LeaderboardMetric.BestWin when win > 0:
                await store.UpsertScoreAsync(Row(win, stake));
                break;
            case LeaderboardMetric.TotalWagered when countStake && stake > 0:
                await store.AddScoreAsync(Row(stake, stake));
                break;
            case LeaderboardMetric.TotalWon when win > 0:
                await store.AddScoreAsync(Row(win, 0));
                break;
            case LeaderboardMetric.RoundsPlayed when countStake && stake > 0:
                await store.AddScoreAsync(Row(1, stake));
                break;
            case LeaderboardMetric.Hits when hits > 0:
                await store.AddScoreAsync(Row(hits, stake));
                break;
        }
    }

    /// <summary>Adds to the player's net loss for the cashback period (negative = a win), weighted for the game, on counting days only.</summary>
    private async Task RecordCashbackAsync(Promotion p, string gameId, string playerId, string username, string currency, decimal netLoss, DateTime at)
    {
        var c = p.Cashback!;
        if (netLoss == 0 || (c.Days.Count > 0 && !c.Days.Contains(at.DayOfWeek))) return;
        var weighted = Math.Round(netLoss * c.WeightFor(gameId) / 100m, 2, MidpointRounding.ToZero);
        if (weighted == 0) return;
        await store.AddScoreAsync(new PromoScore
        {
            PromotionId = p.PromotionId, PeriodKey = PeriodKey(CashbackPeriodOf(c), at), PlayerId = playerId, Username = username,
            Currency = currency, GameId = gameId, Score = weighted, Stake = Math.Max(0, netLoss), AchievedAt = at,
        });
    }

    private static LeaderboardPeriod CashbackPeriodOf(CashbackSettings c) =>
        c.Period == CashbackPeriod.Weekly ? LeaderboardPeriod.Weekly : LeaderboardPeriod.Daily;

    /// <summary>Pays last period's cashback. Idempotent: one award id per (promotion, period, player).</summary>
    private async Task SettleCashbackAsync(DateTime now)
    {
        foreach (var p in store.Promotions.Where(p => p is { Enabled: true, Type: PromoType.Cashback }))
        {
            var c = p.Cashback!;
            var period = CashbackPeriodOf(c);
            var key = PeriodKey(period, period == LeaderboardPeriod.Daily ? now.AddDays(-1) : now.AddDays(-7));
            if (_settledPeriods.Contains((p.PromotionId, "cb:" + key))) continue;

            foreach (var s in await store.TopScoresAsync(p.PromotionId, key, 100_000))
            {
                if (s.Score < Math.Max(0.01m, c.MinLoss)) break; // sorted by net loss, highest first
                var amount = Math.Min(c.MaxPerPlayer, Math.Round(s.Score * c.Percent / 100m, 2, MidpointRounding.ToZero));
                if (amount < 0.01m) continue;
                await AwardCashAsync(p, s.GameId, s.PlayerId, s.Username, s.Currency, amount,
                    Inv($"{c.Percent:0.##}% cashback on {s.Score:0.00} net loss ({key})"), PromoAward.IdFor("cashback", p.PromotionId, key, s.PlayerId), roundId: null);
            }
            _settledPeriods.Add((p.PromotionId, "cb:" + key));
        }
    }

    /// <summary>How a leaderboard score reads: "12.5×", "250.00", "37".</summary>
    public static string ScoreText(LeaderboardMetric metric, decimal score) => metric switch
    {
        LeaderboardMetric.BestMultiple => Inv($"{score}×"),
        LeaderboardMetric.BestWin or LeaderboardMetric.TotalWagered or LeaderboardMetric.TotalWon => Inv($"{score:0.00}"),
        _ => Inv($"{score:0}"),
    };

    // ── Player-facing details of the card / table / cross-game promotions ──

    private async Task<object?> TableDetailsAsync(Promotion p, string playerId, DateTime now)
    {
        switch (p.Type)
        {
            case PromoType.MysteryPrize:
                return new { p.Mystery!.Chance, p.Mystery.Amount, prize = p.Mystery.Prize.ToString() };
            case PromoType.BuyXGetFree:
            {
                var progress = await store.GetProgressAsync(p.PromotionId, playerId);
                return new { p.BuyX!.Buy, count = Math.Min(progress.Count, p.BuyX.Buy) };
            }
            case PromoType.CollectionMission:
            {
                var c = p.Collection!;
                var progress = await store.GetProgressAsync(p.PromotionId, playerId);
                var have = progress.Data.Split(',', StringSplitOptions.RemoveEmptyEntries);
                return new
                {
                    symbols = c.Symbols, collected = c.Symbols.Where(s => have.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList(),
                    c.Reward, prize = c.Prize.ToString(), completed = progress.Completed, c.MaxCompletions, c.ChancePerCard,
                };
            }
            case PromoType.Cashback:
            {
                var c = p.Cashback!;
                var period = CashbackPeriodOf(c);
                var mine = await store.GetScoreAsync(p.PromotionId, PeriodKey(period, now), playerId);
                var net = Math.Max(0, mine?.Score ?? 0);
                return new
                {
                    c.Percent, period = c.Period.ToString(), days = c.Days.Select(d => d.ToString()), c.MaxPerPlayer, c.MinLoss,
                    netLoss = net, due = net >= c.MinLoss ? Math.Min(c.MaxPerPlayer, Math.Round(net * c.Percent / 100m, 2, MidpointRounding.ToZero)) : 0,
                    paidAt = period == LeaderboardPeriod.Daily ? now.Date.AddDays(1)
                        : ISOWeek.ToDateTime(ISOWeek.GetYear(now), ISOWeek.GetWeekOfYear(now), DayOfWeek.Monday).AddDays(7),
                };
            }
            case PromoType.StreakMission:
            {
                var s = p.Streak!;
                var progress = await store.GetProgressAsync(p.PromotionId, playerId);
                var alive = progress.LastAt is { } last && now - last <= TimeSpan.FromMinutes(s.MaxGapMinutes);
                return new
                {
                    counts = s.Counts.ToString(), s.Target, streak = alive ? progress.Streak : 0, s.Reward, prize = s.Prize.ToString(),
                    s.MaxGapMinutes, rewardsLeftToday = Math.Max(0, s.MaxRewardsPerDay - progress.TodayCount(now)),
                };
            }
            case PromoType.LuckyNumber:
                return new { number = LuckyNumberFor(p, now), p.Lucky!.Prize, daily = p.Lucky.Number == null };
            case PromoType.BoostedPayout:
                return new { p.Boost!.Numbers, p.Boost.Payout, p.Boost.MaxBoostedStake };
            case PromoType.HandBonus:
                return new { hand = p.HandBonus!.Hand.ToString(), handName = HandName(p.HandBonus.Hand), p.HandBonus.Prize };
            case PromoType.HandJackpot:
            {
                var j = p.HandJackpot!;
                var left = Math.Max(0, j.Pool - await store.SumCashAsync(p.PromotionId, now.Date));
                return new { hand = j.Hand.ToString(), handName = HandName(j.Hand), pool = left, j.SharePercent };
            }
        }
        return null;
    }
}
