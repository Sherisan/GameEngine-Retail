using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain;

/// <summary>Deterministic swimmer/mine spawn — tier 0=sardine … 3=tuna.</summary>
public record SpawnEvent(int Id, double TimeMs, bool IsBomb, int Tier, double DurationMs, double Y0, int Dir, double Amp, double Phase);

/// <summary>
/// Deterministic swimmer schedule for Feeding Frenzy interactive mode.
/// MUST stay bit-for-bit identical to frontend/src/game/spawnSchedule.ts —
/// both sides seed sfc32 from SHA-256(serverSeedHash:clientSeed:nonce), all
/// public at RoundStarted, so the fish/mines the player sees are exactly
/// what this class validates on EatFish/BombHit.
/// </summary>
public static class SpawnSchedule
{
    public static readonly double[] TierBoosts = { 0.02, 0.05, 0.12, 0.25 };
    public const double MaxBoost = 2.0;

    public static Dictionary<int, SpawnEvent> Generate(string serverSeedHash, string clientSeed, long nonce)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{serverSeedHash}:{clientSeed}:{nonce.ToString(CultureInfo.InvariantCulture)}");
        var digest = SHA256.HashData(bytes);
        var next = Sfc32(
            BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(12, 4)));

        var events = new Dictionary<int, SpawnEvent>();
        double t = 1500;
        var id = 0;
        while (t < 120000 && id < 400)
        {
            var r1 = next();
            var isBomb = r1 < 0.16;
            var r2 = next();
            var tier = r2 < 0.45 ? 0 : r2 < 0.75 ? 1 : r2 < 0.92 ? 2 : 3;
            var r3 = next();
            var durationMs = isBomb ? 14000 + r3 * 8000 : 9000 + r3 * 8000;
            var r4 = next();
            var y0 = 12 + r4 * 66;
            var r5 = next();
            var dir = r5 < 0.5 ? 1 : -1;
            var r6 = next();
            var amp = r6 * 5;
            var r7 = next();
            var phase = r7 * Math.PI * 2;

            events[id] = new SpawnEvent(id, t, isBomb, tier, durationMs, y0, dir, amp, phase);
            id++;

            var r8 = next();
            var interval = 2400 / (1 + t / 20000) * (0.6 + 0.8 * r8);
            t += Math.Max(280, interval);
        }
        return events;
    }

    // Mirrors the client's sfc32 exactly: 32-bit unsigned wraparound arithmetic,
    // same operation order, same closure-captured state across calls.
    private static Func<double> Sfc32(uint a, uint b, uint c, uint d) => () =>
    {
        unchecked
        {
            var t = a + b;
            a = b ^ (b >> 9);
            b = c + (c << 3);
            c = (c << 21) | (c >> 11);
            d += 1;
            t += d;
            c += t;
            return t / 4294967296.0;
        }
    };
}
