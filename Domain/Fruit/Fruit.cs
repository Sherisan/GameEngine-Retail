using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain.Fruit;

/// <summary>One fruit on the menu: <see cref="Bombs"/> out of <see cref="OutOf"/> hide a bomb.</summary>
public sealed record FruitSpec(string Kind, string Name, int Bombs, int OutOf);

/// <summary>
/// JUICE RUSH (Engine = Fruit): fruit is tossed and the player slices it one piece at a time. Any fruit might hide a
/// bomb, with fixed odds per kind (cherry 1 in 10 … dragon fruit 9 in 10). A safe slice multiplies the round's fair
/// value by 1 / P(safe), the fruit's boost; a bomb ends the round with nothing paid. The player can cash out after any
/// safe slice, and reaching ×1000 settles the round automatically.
///
/// The RTP is applied once to the whole round, not per slice:
///     multiplier = floor(97 × Π outOf / Π (outOf − bombs)) / 100      (Π over every safe slice, capped at ×1000)
/// so the cash-out value of any position is RTP / P(reaching it), and no choice of fruit or cash-out timing returns
/// more than 97%. Products are exact (BigInteger). The payout is bet × multiplier, floored to the cent.
///
/// Must match the frontend's src/game/math.ts and src/game/fair.ts (JuiceRush) exactly.
/// </summary>
public static class FruitRules
{
    public const decimal Rtp = 0.97m;
    private const int RtpPercent = 97;

    /// <summary>Top prize. Reaching it settles the round.</summary>
    public const decimal MaxMultiplier = 1000m;
    private static readonly BigInteger MaxCents = new(MaxMultiplier * 100m);

    public static readonly IReadOnlyList<FruitSpec> Menu =
    [
        new("cherry", "Cherry", 1, 10),
        new("lemon", "Lemon", 1, 5),
        new("orange", "Orange", 1, 3),
        new("coconut", "Coconut", 1, 2),
        new("watermelon", "Watermelon", 3, 4),
        new("dragonfruit", "Dragon Fruit", 9, 10),
    ];

    private static readonly Dictionary<string, FruitSpec> ByKind = Menu.ToDictionary(f => f.Kind, StringComparer.Ordinal);

    public static FruitSpec? Find(string? kind) => kind != null && ByKind.TryGetValue(kind, out var fruit) ? fruit : null;

    /// <summary>Cash-out multiplier after these safe slices. 0 before the first one: there's nothing to cash out yet.</summary>
    public static decimal Multiplier(IEnumerable<FruitSpec> safe)
    {
        BigInteger survive = 1, total = 1;
        var any = false;
        foreach (var f in safe)
        {
            survive *= f.OutOf - f.Bombs;
            total *= f.OutOf;
            any = true;
        }
        if (!any) return 0m;
        var cents = BigInteger.Min(RtpPercent * total / survive, MaxCents); // both positive, so the division floors
        return (decimal)cents / 100m;
    }

    public static bool IsMaxed(decimal multiplier) => multiplier >= MaxMultiplier;

    /// <summary>bet × multiplier, floored to the cent. Both have 2 decimals, so the product is exact.</summary>
    public static decimal Payout(decimal bet, decimal multiplier) => Math.Floor(bet * multiplier * 100m) / 100m;
}

/// <summary>
/// Provably fair rolls. Before the bet the player sees SHA-256(serverSeed) (Shoe.Hash); afterwards the seed is revealed.
///   Slice i of the round (0-based), fruit f:
///     roll = Pick(HMAC-SHA256(key: serverSeed, message: "clientSeed:nonce:i"), f.OutOf); a bomb when roll &lt; f.Bombs
///   Pick(mac, n) = (top 52 bits of mac as a big-endian integer × n) >> 52
/// </summary>
public static class FruitFair
{
    public static int Roll(string serverSeed, string clientSeed, long nonce, int index, FruitSpec fruit) =>
        Pick(Mac(serverSeed, $"{clientSeed}:{nonce}:{index}"), fruit.OutOf);

    public static bool IsBomb(FruitSpec fruit, int roll) => roll < fruit.Bombs;

    private static byte[] Mac(string key, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    private static int Pick(byte[] mac, int n) =>
        (int)(((BinaryPrimitives.ReadUInt64BigEndian(mac) >> 12) * (ulong)n) >> 52);
}
