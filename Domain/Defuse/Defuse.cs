using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain.Defuse;

/// <summary>
/// DEFUSE (Engine = Defuse): six wires, one detonator. The player cuts wires one at a time; every safe wire moves them
/// up a rung. They can cash out after any safe cut, and clearing all five safe wires defuses the bomb for the top prize.
///
/// A "rung" is the number of safe wires cleared, cut or revealed by the Bomb Squad Tip. Surviving to rung k has
/// probability (6 − k) / 6, so the multiplier is RTP × 6 / (6 − k), floored to 2 dp: 1.16, 1.45, 1.94, 2.91, 5.82.
///
/// The tip (once per round) reveals one safe wire and counts as a rung. With n live wires it costs bet / (n − 1),
/// which is exactly what a free rung is worth, so RTP stays at 97% whether or not it's used, and whenever it's used.
///
/// Must match the frontend's src/game/math.ts and src/game/fair.ts exactly.
/// </summary>
public static class DefuseRules
{
    public const int WireCount = 6;
    public const int MaxRung = WireCount - 1;
    public const decimal Rtp = 0.97m;

    public static decimal Multiplier(int rung) =>
        rung <= 0 ? 1m : Math.Floor(Rtp * WireCount / (WireCount - rung) * 100m) / 100m;

    public static decimal TopMultiplier => Multiplier(MaxRung);

    public static decimal Payout(decimal bet, int rung) => Math.Round(bet * Multiplier(rung), 2, MidpointRounding.AwayFromZero);

    /// <summary>Tip price at this rung, rounded up to the cent.</summary>
    public static decimal TipCost(decimal bet, int rung) => Math.Ceiling(bet * 100m / (WireCount - rung - 1)) / 100m;
}

/// <summary>
/// Provably fair draws. Before the bet the player sees SHA-256(serverSeed) (Shoe.Hash); afterwards the seed is revealed.
///   Detonator: Pick(HMAC-SHA256(key: serverSeed, message: "clientSeed:nonce"), 6)
///   Tip wire:  candidates[Pick(HMAC-SHA256(serverSeed, "clientSeed:nonce:intel"), candidates.Count)], where
///              candidates = the uncut safe wires in ascending order
///   Pick(mac, n) = (top 52 bits of mac as a big-endian integer × n) >> 52
/// </summary>
public static class DefuseFair
{
    public static int Detonator(string serverSeed, string clientSeed, long nonce) =>
        Pick(Mac(serverSeed, $"{clientSeed}:{nonce}"), DefuseRules.WireCount);

    public static int Intel(string serverSeed, string clientSeed, long nonce, IReadOnlyList<int> candidates) =>
        candidates[Pick(Mac(serverSeed, $"{clientSeed}:{nonce}:intel"), candidates.Count)];

    private static byte[] Mac(string key, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    private static int Pick(byte[] mac, int n) =>
        (int)(((BinaryPrimitives.ReadUInt64BigEndian(mac) >> 12) * (ulong)n) >> 52);
}
