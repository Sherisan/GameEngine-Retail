using System.Security.Cryptography;
using System.Text;
using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Default CSPRNG implementation — provably fair via HMAC-SHA256.
/// Replace via IRNGService registration in Program.cs for operator-specific RNG.
/// </summary>
public class DefaultRNGService : IRNGService
{
    public Task<string> GenerateServerSeedAsync()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Task.FromResult(Convert.ToHexString(bytes).ToLower());
    }

    public string HashServerSeed(string serverSeed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(serverSeed));
        return Convert.ToHexString(bytes).ToLower();
    }

    public Task<decimal> GenerateCrashMultiplierAsync(string serverSeed, string clientSeed, long nonce)
    {
        // Provably fair: HMAC-SHA256(serverSeed, clientSeed + ":" + nonce)
        var key = Encoding.UTF8.GetBytes(serverSeed);
        var message = Encoding.UTF8.GetBytes($"{clientSeed}:{nonce}");
        var hash = HMACSHA256.HashData(key, message);

        // Mask to 31 bits so h is always in [0, 2^31) and denominator stays positive
        var h = (double)(BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF);
        var e = 2_147_483_648.0; // 2^31
        // 96/100 bakes in a ~4% house edge; results below 1.00 are clamped to 1.00x
        var result = Math.Floor(96.0 * e / (e - h)) / 100.0;

        return Task.FromResult((decimal)Math.Max(1.00, result));
    }

    public Task<bool> GenerateGambleOutcomeAsync(string serverSeed, string clientSeed, long nonce, string salt)
    {
        var key = Encoding.UTF8.GetBytes(serverSeed);
        var message = Encoding.UTF8.GetBytes($"{clientSeed}:{nonce}:gamble:{salt}");
        var hash = HMACSHA256.HashData(key, message);
        return Task.FromResult((hash[0] & 1) == 0);
    }
}
