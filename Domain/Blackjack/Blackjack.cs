using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain.Blackjack;

/// <summary>
/// Table rules for Engine = Blackjack. One player against the dealer; each round is dealt from a freshly shuffled shoe.
/// The dealer checks for blackjack when showing an ace or a ten (a dealer blackjack only takes the original bets).
/// Blackjack pays BlackjackPays to 1; split aces get one card each; no insurance or surrender.
/// With the defaults (6 decks, dealer stands on soft 17, 3:2, double after split) the house edge is about 0.5%.
/// </summary>
public class BlackjackSettings
{
    public int Decks { get; set; } = 6;
    public bool DealerHitsSoft17 { get; set; }
    /// <summary>1.5 = 3 to 2, 1.2 = 6 to 5.</summary>
    public decimal BlackjackPays { get; set; } = 1.5m;
    public bool DoubleAfterSplit { get; set; } = true;
    /// <summary>Most hands a player can split into (1 = no splitting).</summary>
    public int MaxHands { get; set; } = 4;

    public void Validate(string gameId)
    {
        void Fail(string msg) => throw new InvalidOperationException($"Games[{gameId}].Blackjack: {msg}");
        if (Decks is < 1 or > 8) Fail("Decks must be 1-8.");
        if (BlackjackPays is < 1.2m or > 1.5m) Fail("Blackjack must pay between 6 to 5 (1.2) and 3 to 2 (1.5).");
        if (MaxHands is < 1 or > 4) Fail("MaxHands must be 1-4.");
    }
}

/// <summary>A playing card. Rank 1 = ace … 11-13 = J, Q, K. Suit 0-3 = spades, hearts, diamonds, clubs.</summary>
public readonly record struct Card(int Rank, int Suit)
{
    private const string Suits = "SHDC";

    /// <summary>Blackjack value: aces 11 (counted down to 1 by Score), court cards 10.</summary>
    public int Value => Rank == 1 ? 11 : Math.Min(Rank, 10);

    /// <summary>"AS", "10H", "QD", "7C".</summary>
    public string Code => (Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => Rank.ToString() }) + Suits[Suit];
}

public static class BlackjackRules
{
    /// <summary>Best total of a hand, and whether an ace is still counting 11 (a "soft" total).</summary>
    public static (int Total, bool Soft) Score(IReadOnlyList<Card> cards)
    {
        var total = cards.Sum(c => c.Value);
        var softAces = cards.Count(c => c.Rank == 1);
        while (total > 21 && softAces > 0)
        {
            total -= 10;
            softAces--;
        }
        return (total, softAces > 0);
    }

    public static bool IsNatural(IReadOnlyList<Card> cards) => cards.Count == 2 && Score(cards).Total == 21;
}

/// <summary>
/// Provably fair shoe. Before the round the player sees SHA-256(serverSeed); afterwards the server seed is revealed.
/// Card order = Fisher–Yates over the ordered shoe (decks × A♠…K♣), position i swapped with
/// j = HMAC-SHA256(key: serverSeed, message: "clientSeed:nonce:i") as a big-endian uint64 mod (i + 1), for i from the top down.
/// </summary>
public static class Shoe
{
    public static List<Card> Shuffle(int decks, string serverSeed, string clientSeed, long nonce)
    {
        var cards = new List<Card>(decks * 52);
        for (var d = 0; d < decks; d++)
            for (var s = 0; s < 4; s++)
                for (var r = 1; r <= 13; r++)
                    cards.Add(new Card(r, s));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(serverSeed));
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{clientSeed}:{nonce}:{i}"));
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(mac);
            var j = (int)(value % (ulong)(i + 1));
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        return cards;
    }

    public static string NewSeed() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string serverSeed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverSeed))).ToLowerInvariant();
}
