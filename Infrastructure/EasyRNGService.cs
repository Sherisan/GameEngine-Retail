using System.Security.Cryptography;
using System.Text;
using EasyRNG;
using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Wraps EasyRNG's ProvablyFairCrashMultiplierGenerator (formerly GameOnStudiosRandomNumberGenerator — renamed, same code).
///
/// The library is designed around a long-lived generator that plays many rounds per
/// server-seed batch and only reveals the seed when the batch rotates. This codebase's
/// IRNGService contract instead generates and reveals a fresh server seed every single
/// round (GameEngine.RunWaitingPhaseAsync). To plug the library in without restructuring
/// Round/GameEngine, each round is treated as its own one-round batch: a fresh generator
/// is rotated per round and its seed is revealed immediately at crash, same as
/// DefaultRNGService did. This is not the library's intended (and more efficient) batching
/// mode — switch to a long-lived per-table instance with periodic RotateServerSeed() if you
/// want the seed to only be revealed every N rounds.
///
/// Not thread-safe across concurrent rounds — fine here because GameEngine runs one round
/// at a time on a single game loop.
/// </summary>
public class EasyRNGService : IRNGService
{
    private const double HouseEdgePercent = 4.0;
    private const decimal MaxMultiplier = 10000m;

    private ProvablyFairCrashMultiplierGenerator? _current;

    // Long-lived, thread-safe — owns its own independent commit-reveal seed batch, separate
    // from the per-round crash seed (the gamble feature isn't tied to any particular round's
    // seed, so it uses EasyRNG's other generator, built for exactly this "call on demand,
    // pick from a weighted table" shape). Equal weights make it a fair coin flip:
    //   2x -> gamble won (caller doubles the wagered winnings)
    //   1x -> gamble lost (caller forfeits the wagered winnings)
    private static readonly IReadOnlyList<MultiplierTableEntry> GambleTable = new List<MultiplierTableEntry>
    {
        new(2m, 1),
        new(1m, 1),
    };
    private readonly ProvablyFairSlotMultiplierGenerator _gamble = new(GambleTable);

    public Task<string> GenerateServerSeedAsync()
    {
        _current = new ProvablyFairCrashMultiplierGenerator(houseEdgePercent: HouseEdgePercent, maxMultiplier: MaxMultiplier);
        return Task.FromResult(_current.RevealServerSeed());
    }

    public string HashServerSeed(string serverSeed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverSeed))).ToLowerInvariant();

    public Task<decimal> GenerateCrashMultiplierAsync(string serverSeed, string clientSeed, long nonce)
    {
        if (_current is null || _current.RevealServerSeed() != serverSeed)
            throw new InvalidOperationException(
                "GenerateCrashMultiplierAsync must be called for the same round immediately after GenerateServerSeedAsync.");

        _current.ClientSeed = clientSeed;
        var result = _current.PlayRound();
        return Task.FromResult(result.CrashMultiplier);
    }

    // The round's serverSeed/clientSeed/nonce/salt aren't used here — ProvablyFairSlotMultiplierGenerator
    // manages its own seed/nonce internally and every GenerateMultiplier() call is already unique.
    public Task<bool> GenerateGambleOutcomeAsync(string serverSeed, string clientSeed, long nonce, string salt)
    {
        var result = _gamble.GenerateMultiplier();
        return Task.FromResult(result.Multiplier == 2m);
    }
}
