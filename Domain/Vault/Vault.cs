using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain.Vault;

/// <summary>
/// VAULT CRACKER (Engine = Vault): a five-digit combination, one ring of four tumblers per digit, and one tumbler on
/// every ring trips the alarm. Each digit is a fresh 1-in-4 risk; the player can cash out after any cracked digit, and
/// cracking all five opens the vault for the top prize.
///
/// A "rung" is the number of digits cracked, by the player or by the Stethoscope. Reaching rung k has probability
/// (3/4)^k, so the multiplier is RTP × (4/3)^k, floored to 2 dp: 1.29, 1.72, 2.29, 3.06, 4.08.
///
/// The Stethoscope (once per round) cracks the current digit with no risk. It costs the value of that rung divided by
/// the RTP (IntelCost), so the Stethoscope itself returns 97% whenever it is bought.
///
/// Must match the frontend's src/game/math.ts and src/game/fair.ts (VaultCracker) exactly.
/// </summary>
public static class VaultRules
{
    public const int Steps = 5;
    public const int Tumblers = 4;
    public const decimal Rtp = 0.97m;

    public static decimal Multiplier(int rung)
    {
        if (rung <= 0) return 1m;
        decimal up = 1m, down = 1m;
        for (var i = 0; i < rung; i++) { up *= Tumblers; down *= Tumblers - 1; }
        return Math.Floor(Rtp * 100m * up / down) / 100m;
    }

    public static decimal TopMultiplier => Multiplier(Steps);

    public static decimal Payout(decimal bet, int rung) => Math.Round(bet * Multiplier(rung), 2, MidpointRounding.AwayFromZero);

    /// <summary>Stethoscope price at this rung, rounded up to the cent: 29.9%, 44.3%, 58.8%, 79.4%, 105.2% of the bet.</summary>
    public static decimal IntelCost(decimal bet, int rung) =>
        Math.Ceiling(bet * (Multiplier(rung + 1) - Multiplier(rung)) / Rtp * 100m) / 100m;
}

/// <summary>
/// Provably fair draws. Before the bet the player sees SHA-256(serverSeed) (Shoe.Hash); afterwards the seed is revealed.
///   Alarm on ring k (0-4): Pick(HMAC-SHA256(key: serverSeed, message: "clientSeed:nonce:k"), 4)
///   Stethoscope:           candidates[Pick(HMAC-SHA256(serverSeed, "clientSeed:nonce:intel"), candidates.Count)], where
///                          candidates = the safe tumblers of the ring being cracked, ascending
///   Pick(mac, n) = (top 52 bits of mac as a big-endian integer × n) >> 52
/// </summary>
public static class VaultFair
{
    public static int[] Alarms(string serverSeed, string clientSeed, long nonce) =>
        Enumerable.Range(0, VaultRules.Steps)
            .Select(k => Pick(Mac(serverSeed, $"{clientSeed}:{nonce}:{k}"), VaultRules.Tumblers))
            .ToArray();

    public static int Intel(string serverSeed, string clientSeed, long nonce, IReadOnlyList<int> candidates) =>
        candidates[Pick(Mac(serverSeed, $"{clientSeed}:{nonce}:intel"), candidates.Count)];

    private static byte[] Mac(string key, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    private static int Pick(byte[] mac, int n) =>
        (int)(((BinaryPrimitives.ReadUInt64BigEndian(mac) >> 12) * (ulong)n) >> 52);
}
