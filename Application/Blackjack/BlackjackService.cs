using System.Collections.Concurrent;
using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.Blackjack;
using Worldiator.Server.Domain.Promotions;

namespace Worldiator.Server.Application.Blackjack;

/// <summary>
/// On-demand blackjack (Engine = Blackjack): one player against the dealer, one round at a time per player and game.
///
/// Deal: the stake is debited ("{round}-bet"), the shoe is shuffled from the player's committed server seed (its hash
/// was shown before the deal — see Shoe) and the client seed, and the cards go out. Naturals settle at once.
/// Hit / stand / double / split act on the active hand; a double or split debits the extra stake ("{round}-dbl{n}",
/// "{round}-split{n}"). When every hand is done the dealer plays and the round is credited once ("{round}-win").
/// A free-bet round uses the free bet as the stake (no doubling or splitting) and is paid by the promotion; a push
/// gives the free bet back. Promotions see every real-money round (PromoEngine.PlayedAsync).
///
/// Rounds live in memory (single node). A round left unfinished for 10 minutes is stood and settled automatically.
/// </summary>
public sealed class BlackjackService(
    IServiceScopeFactory scopes,
    PromoEngine promos,
    ILogger<BlackjackService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Seat> _seats = new();

    private sealed class Seat
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public required PlayerSession Player { get; init; }
        public string NextServerSeed { get; set; } = Shoe.NewSeed();
        public long Nonce { get; set; }
        public Round? Round { get; set; }
    }

    private sealed class Hand
    {
        public List<Card> Cards { get; } = [];
        public decimal Stake { get; set; }
        public bool Doubled { get; set; }
        public bool FromSplit { get; init; }
        public bool SplitAces { get; init; }
        public bool Done { get; set; }
        public string? Result { get; set; }
        public decimal Payout { get; set; }
    }

    private sealed class Round
    {
        public required string RoundId { get; init; }
        public required GameDefinition Game { get; init; }
        public required BlackjackSettings Rules { get; init; }
        public required List<Card> Shoe { get; init; }
        public required string ServerSeed { get; init; }
        public required string ClientSeed { get; init; }
        public required long Nonce { get; init; }
        public PromoAward? FreeBet { get; init; }
        public int Next { get; set; }
        public List<Card> Dealer { get; } = [];
        public List<Hand> Hands { get; } = [];
        public int Active { get; set; }
        public bool Settled { get; set; }
        public decimal Win { get; set; }
        public decimal? Balance { get; set; }
        public bool WinPending { get; set; }
        public string? Message { get; set; }
        public IReadOnlyList<PromoOutcome> Promos { get; set; } = [];
        public DateTime LastAt { get; set; } = DateTime.UtcNow;

        public Card Draw() => Shoe[Next++];
        public decimal TotalStake => Hands.Sum(h => h.Stake);
    }

    private static string Key(string gameId, string playerId) => $"{gameId.ToLowerInvariant()}|{playerId}";

    private Seat SeatFor(GameDefinition game, PlayerSession player) =>
        _seats.GetOrAdd(Key(game.GameId, player.PlayerId), _ => new Seat { Player = player });

    // ── Public API (BlackjackController) ──────────────────────────────────

    public async Task<object> StateAsync(GameDefinition game, PlayerSession player)
    {
        var seat = SeatFor(game, player);
        await seat.Gate.WaitAsync();
        try { return Dto(seat); }
        finally { seat.Gate.Release(); }
    }

    public async Task<(object? State, WalletResult? Error)> DealAsync(GameDefinition game, PlayerSession player, decimal stake, bool freeBet, string? clientSeed)
    {
        var seat = SeatFor(game, player);
        await seat.Gate.WaitAsync();
        try
        {
            if (seat.Round is { Settled: false }) return Bad("ROUND_IN_PLAY", "Finish the hand in play first.");
            clientSeed = string.IsNullOrWhiteSpace(clientSeed) ? Shoe.NewSeed()[..16] : clientSeed.Trim();
            if (clientSeed.Length > 64) return Bad("INVALID_SEED", "The client seed can be at most 64 characters.");

            PromoAward? free = null;
            if (freeBet)
            {
                free = await promos.ClaimFreeBetAsync(player, game.GameId);
                if (free == null) return Bad("NO_FREE_BET", "You have no free bet for this game.");
                stake = free.Amount;
            }
            else if (stake <= 0 || stake != Math.Round(stake, 2))
                return Bad("INVALID_AMOUNT", "The stake must be a positive amount with at most 2 decimals.");

            var roundId = Guid.NewGuid().ToString("N");
            decimal? balance = null;
            if (free == null)
            {
                using var scope = scopes.CreateScope();
                var debit = await scope.ServiceProvider.GetRequiredService<WalletService>().DebitAsync(game, player, roundId, $"{roundId}-bet", stake);
                if (!debit.Ok) return (null, debit);
                balance = debit.Balance;
            }

            var rules = game.Blackjack ?? new BlackjackSettings();
            seat.Nonce++;
            var round = new Round
            {
                RoundId = roundId, Game = game, Rules = rules, FreeBet = free,
                ServerSeed = seat.NextServerSeed, ClientSeed = clientSeed, Nonce = seat.Nonce,
                Shoe = Shoe.Shuffle(rules.Decks, seat.NextServerSeed, clientSeed, seat.Nonce),
                Balance = balance,
            };
            seat.NextServerSeed = Shoe.NewSeed();
            seat.Round = round;

            var hand = new Hand { Stake = stake };
            round.Hands.Add(hand);
            hand.Cards.Add(round.Draw());
            round.Dealer.Add(round.Draw());
            hand.Cards.Add(round.Draw());
            round.Dealer.Add(round.Draw());

            // The dealer checks for blackjack under an ace or a ten; either natural ends the round now.
            if (BlackjackRules.IsNatural(round.Dealer) || BlackjackRules.IsNatural(hand.Cards))
            {
                hand.Done = true;
                await SettleAsync(seat, round);
            }

            logger.LogInformation("Blackjack deal: game={GameId} player={PlayerId} round={RoundId} stake={Stake} free={Free} seedHash={Hash} nonce={Nonce}",
                game.GameId, player.PlayerId, roundId, stake, free != null, Shoe.Hash(round.ServerSeed), round.Nonce);
            return (Dto(seat), null);
        }
        finally { seat.Gate.Release(); }
    }

    public async Task<(object? State, WalletResult? Error)> ActAsync(GameDefinition game, PlayerSession player, string action)
    {
        var seat = SeatFor(game, player);
        await seat.Gate.WaitAsync();
        try
        {
            if (seat.Round is not { Settled: false } round) return Bad("NO_ROUND", "There is no hand in play — deal first.");
            var hand = round.Hands[round.Active];
            round.LastAt = DateTime.UtcNow;

            switch ((action ?? "").ToLowerInvariant())
            {
                case "hit":
                    hand.Cards.Add(round.Draw());
                    if (BlackjackRules.Score(hand.Cards).Total >= 21) hand.Done = true;
                    break;

                case "stand":
                    hand.Done = true;
                    break;

                case "double":
                {
                    if (!CanDouble(round, hand)) return Bad("NOT_ALLOWED", "You can't double this hand.");
                    var error = await DebitExtraAsync(round, player, $"{round.RoundId}-dbl{round.Active}", hand.Stake);
                    if (error != null) return (null, error);
                    hand.Stake *= 2;
                    hand.Doubled = true;
                    hand.Cards.Add(round.Draw());
                    hand.Done = true;
                    break;
                }

                case "split":
                {
                    if (!CanSplit(round, hand)) return Bad("NOT_ALLOWED", "You can't split this hand.");
                    var error = await DebitExtraAsync(round, player, $"{round.RoundId}-split{round.Hands.Count}", hand.Stake);
                    if (error != null) return (null, error);
                    var aces = hand.Cards[0].Rank == 1;
                    var second = new Hand { Stake = hand.Stake, FromSplit = true, SplitAces = aces };
                    second.Cards.Add(hand.Cards[1]);
                    var first = new Hand { Stake = hand.Stake, FromSplit = true, SplitAces = aces };
                    first.Cards.Add(hand.Cards[0]);
                    round.Hands[round.Active] = first;
                    round.Hands.Insert(round.Active + 1, second);
                    first.Cards.Add(round.Draw());
                    second.Cards.Add(round.Draw());
                    // Split aces take one card each; any hand that reached 21 stands.
                    foreach (var h in new[] { first, second })
                        if (aces || BlackjackRules.Score(h.Cards).Total == 21) h.Done = true;
                    break;
                }

                default:
                    return Bad("INVALID_ACTION", "Action must be hit, stand, double or split.");
            }

            while (round.Active < round.Hands.Count && round.Hands[round.Active].Done) round.Active++;
            if (round.Active >= round.Hands.Count) await SettleAsync(seat, round);
            return (Dto(seat), null);
        }
        finally { seat.Gate.Release(); }
    }

    private static bool CanDouble(Round round, Hand hand) =>
        round.FreeBet == null && hand.Cards.Count == 2 && !hand.SplitAces && (!hand.FromSplit || round.Rules.DoubleAfterSplit);

    private static bool CanSplit(Round round, Hand hand) =>
        round.FreeBet == null && hand.Cards.Count == 2 && hand.Cards[0].Value == hand.Cards[1].Value
        && round.Hands.Count < round.Rules.MaxHands && !hand.SplitAces;

    private async Task<WalletResult?> DebitExtraAsync(Round round, PlayerSession player, string txId, decimal amount)
    {
        using var scope = scopes.CreateScope();
        var debit = await scope.ServiceProvider.GetRequiredService<WalletService>().DebitAsync(round.Game, player, round.RoundId, txId, amount);
        if (!debit.Ok) return debit;
        round.Balance = debit.Balance;
        return null;
    }

    // ── Dealer and settlement ─────────────────────────────────────────────

    private async Task SettleAsync(Seat seat, Round round)
    {
        var rules = round.Rules;
        var dealerNatural = BlackjackRules.IsNatural(round.Dealer);
        var onlyHand = round.Hands.Count == 1;

        // The dealer only draws if some hand is still standing (not bust, not a natural that's already paid).
        var live = round.Hands.Any(h => BlackjackRules.Score(h.Cards).Total <= 21 && !(onlyHand && BlackjackRules.IsNatural(h.Cards)));
        if (live && !dealerNatural)
        {
            while (true)
            {
                var (total, soft) = BlackjackRules.Score(round.Dealer);
                if (total > 17 || (total == 17 && !(soft && rules.DealerHitsSoft17))) break;
                round.Dealer.Add(round.Draw());
            }
        }

        var dealer = BlackjackRules.Score(round.Dealer).Total;
        foreach (var h in round.Hands)
        {
            var total = BlackjackRules.Score(h.Cards).Total;
            var natural = onlyHand && !h.FromSplit && BlackjackRules.IsNatural(h.Cards);
            (h.Result, h.Payout) =
                natural && dealerNatural ? ("push", h.Stake)
                : natural ? ("blackjack", Math.Round(h.Stake * (1 + rules.BlackjackPays), 2, MidpointRounding.ToZero))
                : dealerNatural ? ("lose", 0m)
                : total > 21 ? ("bust", 0m)
                : dealer > 21 || total > dealer ? ("win", h.Stake * 2)
                : total == dealer ? ("push", h.Stake)
                : ("lose", 0m);
            h.Done = true;
        }

        round.Settled = true;
        round.Active = round.Hands.Count;
        var payout = Math.Min(round.Hands.Sum(h => h.Payout), round.Game.MaxWin);
        var player = seat.Player;

        if (round.FreeBet != null)
        {
            if (round.Hands[0].Result == "push")
            {
                await promos.ReleaseFreeBetAsync(round.FreeBet);
                round.Message = "Push — you keep your free bet.";
            }
            else round.Win = await promos.SettleFreeBetAsync(round.FreeBet, round.RoundId, payout);
            round.Balance = await TryBalanceAsync(round.Game, player);
            return;
        }

        round.Win = payout;
        if (payout > 0)
        {
            using var scope = scopes.CreateScope();
            var credit = await scope.ServiceProvider.GetRequiredService<WalletService>().CreditAsync(round.Game, player, round.RoundId, $"{round.RoundId}-win", payout);
            if (credit.Ok) round.Balance = credit.Balance;
            else
            {
                round.WinPending = true;
                logger.LogCritical("Blackjack win NOT credited: game={GameId} player={PlayerId} round={RoundId} win={Win} code={Code} error={Error}",
                    round.Game.GameId, player.PlayerId, round.RoundId, payout, credit.ErrorCode, credit.Error);
            }
        }
        round.Balance ??= await TryBalanceAsync(round.Game, player);

        round.Promos = await promos.PlayedAsync(new PlayReport(round.Game.GameId, player, round.RoundId, round.TotalStake, round.WinPending ? 0 : payout)
        {
            Hits = round.Hands.Count(h => h.Result == "blackjack"),
            Hands = SpecialHands(round),
        });

        logger.LogInformation("Blackjack settle: game={GameId} player={PlayerId} round={RoundId} stake={Stake} win={Win} dealer={Dealer} hands={Hands}",
            round.Game.GameId, player.PlayerId, round.RoundId, round.TotalStake, payout,
            string.Join(' ', round.Dealer.Select(c => c.Code)), string.Join(" | ", round.Hands.Select(h => string.Join(' ', h.Cards.Select(c => c.Code)) + " " + h.Result)));
    }

    /// <summary>The special hands promotions pay on, from the player's first two cards (and the third, for 7-7-7) and every hand.</summary>
    private static HashSet<SpecialHand> SpecialHands(Round round)
    {
        var set = new HashSet<SpecialHand>();
        var first = round.Hands[0].Cards;
        // After a split the first hand's second card isn't the dealt one, so pair hands only count unsplit.
        var dealt = round.Hands.Count == 1 ? first : [];
        if (round.Hands.Any(h => h.Result == "blackjack")) set.Add(SpecialHand.Blackjack);
        if (round.Hands.Any(h => h.Result == "blackjack") && first.Count == 2 && first[0].Suit == first[1].Suit) set.Add(SpecialHand.SuitedBlackjack);
        if (dealt.Count >= 2 && dealt[0].Rank == 7 && dealt[1].Rank == 7 && dealt[0].Suit == dealt[1].Suit) set.Add(SpecialHand.SuitedSevens);
        if (dealt.Count >= 3 && dealt.Take(3).All(c => c.Rank == 7)) set.Add(SpecialHand.ThreeSevens);
        if (dealt.Count >= 2 && dealt[0] == dealt[1]) set.Add(SpecialHand.PerfectPair);
        if (round.Hands.Any(h => h.Cards.Count >= 5 && BlackjackRules.Score(h.Cards).Total <= 21)) set.Add(SpecialHand.FiveCardCharlie);
        return set;
    }

    private async Task<decimal?> TryBalanceAsync(GameDefinition game, PlayerSession player)
    {
        try
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WalletService>().GetBalanceAsync(player with { GameId = game.GameId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Blackjack balance lookup failed for {PlayerId}", player.PlayerId);
            return null;
        }
    }

    // ── What the table shows ──────────────────────────────────────────────

    private static object Dto(Seat seat)
    {
        var next = Shoe.Hash(seat.NextServerSeed);
        if (seat.Round is not { } r) return new { round = (object?)null, nextServerSeedHash = next };

        var revealed = r.Settled;
        var dealerCards = revealed ? r.Dealer : [r.Dealer[0]];
        var active = !r.Settled && r.Active < r.Hands.Count ? r.Hands[r.Active] : null;
        var actions = active == null ? [] : new List<string> { "hit", "stand" };
        if (active != null && CanDouble(r, active)) actions.Add("double");
        if (active != null && CanSplit(r, active)) actions.Add("split");

        return new
        {
            round = new
            {
                r.RoundId,
                status = r.Settled ? "SETTLED" : "PLAYER_TURN",
                freeBet = r.FreeBet != null,
                stake = r.TotalStake,
                dealer = new
                {
                    cards = revealed ? r.Dealer.Select(c => c.Code).ToList() : [r.Dealer[0].Code, null],
                    total = BlackjackRules.Score(dealerCards).Total,
                    blackjack = revealed && BlackjackRules.IsNatural(r.Dealer),
                },
                hands = r.Hands.Select(h =>
                {
                    var (total, soft) = BlackjackRules.Score(h.Cards);
                    return new { cards = h.Cards.Select(c => c.Code), total, soft, h.Stake, h.Doubled, h.Done, h.Result, h.Payout };
                }),
                active = r.Settled ? (int?)null : r.Active,
                actions,
                r.Win,
                r.Balance,
                r.WinPending,
                r.Message,
                promos = r.Promos,
                proof = new
                {
                    serverSeedHash = Shoe.Hash(r.ServerSeed),
                    serverSeed = r.Settled ? r.ServerSeed : null,
                    r.ClientSeed,
                    r.Nonce,
                },
            },
            nextServerSeedHash = next,
        };
    }

    private static (object?, WalletResult?) Bad(string code, string message) =>
        (null, WalletResult.Fail(WalletErrorKind.BadRequest, code, message));

    // ── Abandoned rounds ──────────────────────────────────────────────────

    /// <summary>Every minute: rounds idle for 10 minutes are stood and settled, so a stake is never left hanging.</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var seat in _seats.Values)
                {
                    if (seat.Round is not { Settled: false } r || DateTime.UtcNow - r.LastAt < IdleTimeout) continue;
                    await seat.Gate.WaitAsync(ct);
                    try
                    {
                        if (seat.Round is not { Settled: false } round) continue;
                        foreach (var h in round.Hands) h.Done = true;
                        await SettleAsync(seat, round);
                        logger.LogInformation("Blackjack round {RoundId} for {PlayerId} was idle — stood and settled", round.RoundId, seat.Player.PlayerId);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Blackjack idle settle failed for {PlayerId}", seat.Player.PlayerId); }
                    finally { seat.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
