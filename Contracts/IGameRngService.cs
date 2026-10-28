using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>Provable-fairness data for one draw. The server seed behind ServerSeedHash is revealed when it rotates.</summary>
public record RngProof(string ServerSeedHash, string? ClientSeed, long Nonce);

/// <summary>One RNG draw. Value is the crash point (Target) or the index into Outcomes / Options (Multiplier / Pick).</summary>
public record RngDraw(decimal Value, RngProof Proof);

/// <summary>A server seed that has been retired and revealed, so draws made under it can be verified.</summary>
public record RevealedSeed(string ServerSeed, string ServerSeedHash, DateTime RevealedAt);

/// <summary>
/// RNG for the generic play models (see RngSettings). One long-lived generator per game,
/// so every game has its own seed batch.
/// </summary>
public interface IGameRngService
{
    RngDraw Draw(GameDefinition game);

    /// <summary>Hash of the seed currently in use, and the last seed revealed for this game.</summary>
    (string CurrentServerSeedHash, RevealedSeed? Previous) GetSeedInfo(GameDefinition game);
}
