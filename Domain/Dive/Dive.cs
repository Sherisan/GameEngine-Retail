using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain.Dive;

/// <summary>
/// DEEP DIVE (Engine = Dive): eight depth zones, three caves per zone, and a shark hides in one cave of every zone. Each
/// zone is a fresh 1-in-3 risk; the player can surface (cash out) after any cleared zone, and clearing all eight reaches
/// the treasure at the abyss for the top prize.
///
/// A "rung" is the number of zones cleared. Reaching rung k has probability (2/3)^k, so the multiplier is
/// RTP × (3/2)^k, floored to 2 dp: 1.45, 2.18, 3.27, 4.91, 7.36, 11.04, 16.57, 24.86.
///
/// Must match the frontend's src/game/math.ts and src/game/fair.ts (DeepDive) exactly.
/// </summary>
public static class DiveRules
{
    public const int Zones = 8;
    public const int Caves = 3;
    public const decimal Rtp = 0.97m;

    public static decimal Multiplier(int rung)
    {
        if (rung <= 0) return 1m;
        decimal up = 1m, down = 1m;
        for (var i = 0; i < rung; i++) { up *= Caves; down *= Caves - 1; }
        return Math.Floor(Rtp * 100m * up / down) / 100m;
    }

    public static decimal TopMultiplier => Multiplier(Zones);

    public static decimal Payout(decimal bet, int rung) => Math.Round(bet * Multiplier(rung), 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Provably fair draws. Before the bet the player sees SHA-256(serverSeed) (Shoe.Hash); afterwards the seed is revealed.
///   Shark cave in zone k (0-7): Pick(HMAC-SHA256(key: serverSeed, message: "clientSeed:nonce:k"), 3)
///   Pick(mac, n) = (top 52 bits of mac as a big-endian integer × n) >> 52
/// </summary>
public static class DiveFair
{
    public static int[] Sharks(string serverSeed, string clientSeed, long nonce) =>
        Enumerable.Range(0, DiveRules.Zones)
            .Select(k => Pick(Mac(serverSeed, $"{clientSeed}:{nonce}:{k}"), DiveRules.Caves))
            .ToArray();

    private static byte[] Mac(string key, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    private static int Pick(byte[] mac, int n) =>
        (int)(((BinaryPrimitives.ReadUInt64BigEndian(mac) >> 12) * (ulong)n) >> 52);
}
