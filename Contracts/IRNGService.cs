namespace Worldiator.Server.Contracts;

/// <summary>
/// Pluggable RNG contract. The other dev implements this with the real CSPRNG.
/// DefaultRNGService is the placeholder — uses HMAC-SHA256 and is provably fair.
///
/// HOW TO PLUG IN YOUR RNG:
///   1. Implement IRNGService (e.g. OperatorRNGService)
///   2. Register in Program.cs: builder.Services.AddSingleton&lt;IRNGService, OperatorRNGService&gt;()
/// </summary>
public interface IRNGService
{
    /// <summary>Generate the crash multiplier. Must be unpredictable, provably fair, and >= 1.00.</summary>
    Task<decimal> GenerateCrashMultiplierAsync(string serverSeed, string clientSeed, long nonce);

    /// <summary>Generate a server seed. Its hash is shown to players before the round (provable fairness).</summary>
    Task<string> GenerateServerSeedAsync();

    /// <summary>SHA-256 hash of the server seed — shown at round start, revealed at crash.</summary>
    string HashServerSeed(string serverSeed);

    /// <summary>
    /// Provably fair 50/50 outcome (e.g. the post-cashout gamble feature). The round's
    /// serverSeed/clientSeed/nonce are passed for implementations that want to derive the
    /// result from the round's already-revealed seed; salt (e.g. the bet id) keeps repeated
    /// calls distinct for those implementations. Not all implementations need these — a
    /// provider with its own independent commit-reveal seed batch for this feature may ignore
    /// them entirely, since each call to its own generator is already unique.
    /// </summary>
    Task<bool> GenerateGambleOutcomeAsync(string serverSeed, string clientSeed, long nonce, string salt);
}
