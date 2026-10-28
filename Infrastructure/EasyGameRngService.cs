using System.Collections.Concurrent;
using EasyRNG;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// IGameRngService on EasyRNG, using the library the way it is designed: one long-lived generator
/// per game playing many draws per server-seed batch, rotated (and the old seed revealed) every
/// RotateSeedEvery draws.
///   Multiplier / Pick → ProvablyFairSlotMultiplierGenerator over the outcomes / options (draw = table index)
///   Target            → ProvablyFairCrashMultiplierGenerator (draw = crash point)
/// Generators aren't documented as thread-safe, so each is used under its own lock.
/// </summary>
public class EasyGameRngService(ILogger<EasyGameRngService> logger) : IGameRngService
{
    private readonly ConcurrentDictionary<string, GameGenerator> _generators = new(StringComparer.OrdinalIgnoreCase);

    public RngDraw Draw(GameDefinition game)
    {
        var gen = _generators.GetOrAdd(game.GameId, _ => GameGenerator.Create(game));
        lock (gen)
        {
            var draw = gen.Next();
            if (++gen.DrawsSinceRotation >= game.Rng!.RotateSeedEvery)
                Rotate(game.GameId, gen);
            return draw;
        }
    }

    public (string CurrentServerSeedHash, RevealedSeed? Previous) GetSeedInfo(GameDefinition game)
    {
        var gen = _generators.GetOrAdd(game.GameId, _ => GameGenerator.Create(game));
        lock (gen) return (gen.ServerSeedHash, gen.Previous);
    }

    private void Rotate(string gameId, GameGenerator gen)
    {
        var revealed = new RevealedSeed(gen.RevealServerSeed(), gen.ServerSeedHash, DateTime.UtcNow);
        gen.RotateServerSeed();
        gen.Previous = revealed;
        gen.DrawsSinceRotation = 0;
        // Logged so every revealed seed is kept even across restarts (players can verify against it).
        logger.LogInformation("RNG seed rotated: game={GameId} revealedSeed={Seed} hash={Hash}",
            gameId, revealed.ServerSeed, revealed.ServerSeedHash);
    }

    private sealed class GameGenerator
    {
        public required Func<RngDraw> Next { get; init; }
        public required Func<string> RevealServerSeed { get; init; }
        public required Action RotateServerSeed { get; init; }
        public required Func<string> GetServerSeedHash { get; init; }
        public string ServerSeedHash => GetServerSeedHash();
        public int DrawsSinceRotation { get; set; }
        public RevealedSeed? Previous { get; set; }

        public static GameGenerator Create(GameDefinition game)
        {
            var rng = game.Rng ?? throw new InvalidOperationException($"Game \"{game.GameId}\" has no Rng settings.");
            switch (rng.Model)
            {
                case RngModel.Target:
                {
                    var g = new ProvablyFairCrashMultiplierGenerator(houseEdgePercent: rng.HouseEdgePercent, maxMultiplier: rng.MaxMultiplier);
                    return new GameGenerator
                    {
                        Next = () =>
                        {
                            var r = g.PlayRound();
                            return new RngDraw(r.CrashMultiplier, new RngProof(r.ServerSeedHash, r.ClientSeed, r.Nonce));
                        },
                        RevealServerSeed = g.RevealServerSeed,
                        RotateServerSeed = g.RotateServerSeed,
                        GetServerSeedHash = () => g.ServerSeedHash,
                    };
                }
                default:
                {
                    // EasyRNG requires positive, unique multipliers, so entry i is encoded as multiplier i + 1
                    // with the configured weight (a 0x loss can't go in directly). Verifying a draw uses this
                    // same encoded table.
                    var weights = rng.Model == RngModel.Pick
                        ? rng.Options.Select(o => o.Weight)
                        : rng.Outcomes.Select(o => o.Weight);
                    var table = weights.Select((w, i) => new MultiplierTableEntry(i + 1, w)).ToList();
                    var g = new ProvablyFairSlotMultiplierGenerator(table);
                    return new GameGenerator
                    {
                        Next = () =>
                        {
                            var r = g.GenerateMultiplier();
                            return new RngDraw(r.Multiplier - 1, new RngProof(r.ServerSeedHash, r.ClientSeed, r.Nonce));
                        },
                        RevealServerSeed = g.RevealServerSeed,
                        RotateServerSeed = g.RotateServerSeed,
                        GetServerSeedHash = () => g.ServerSeedHash,
                    };
                }
            }
        }
    }
}
