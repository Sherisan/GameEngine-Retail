using System.Collections.Concurrent;
using System.Text.Json;
using EasyRNG;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// IGameRngService on EasyRNG, using the library the way it is designed: one long-lived generator
/// per game playing many draws per server-seed batch, rotated (and the old seed revealed) every
/// RotateSeedEvery draws.
///   Multiplier / Pick → ProvablyFairSlotMultiplierGenerator over the outcomes / options (draw = table index)
///   Wheel             → the same, over Pockets equal weights (draw = pocket number)
///   Target            → ProvablyFairCrashMultiplierGenerator (draw = crash point)
/// Generators aren't documented as thread-safe, so each is used under its own lock.
/// </summary>
public class EasyGameRngService(ILogger<EasyGameRngService> logger) : IGameRngService
{
    private readonly ConcurrentDictionary<string, GameGenerator> _generators = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _replaceLock = new();

    public RngDraw Draw(GameDefinition game)
    {
        while (true)
        {
            var gen = GeneratorFor(game);
            lock (gen)
            {
                if (gen.Retired) continue; // replaced while we waited — its seed is public now, draw from the new one
                var draw = gen.Next();
                if (++gen.DrawsSinceRotation >= game.Rng!.RotateSeedEvery)
                    Rotate(game.GameId, gen);
                return draw;
            }
        }
    }

    public (string CurrentServerSeedHash, RevealedSeed? Previous) GetSeedInfo(GameDefinition game)
    {
        var gen = GeneratorFor(game);
        lock (gen) return (gen.ServerSeedHash, gen.Previous);
    }

    /// <summary>
    /// The game's generator, rebuilt when its Rng settings were changed in the back office: the old one is retired
    /// and its seed revealed (as on a normal rotation), so every draw is verifiable against the table it used.
    /// </summary>
    private GameGenerator GeneratorFor(GameDefinition game)
    {
        var gen = _generators.GetOrAdd(game.GameId, _ => GameGenerator.Create(game));
        // Definitions are immutable snapshots, so the same Rng instance means the same settings.
        if (ReferenceEquals(gen.Settings, game.Rng)) return gen;
        var fingerprint = GameGenerator.Fingerprint(game.Rng);
        if (gen.SettingsFingerprint == fingerprint) return gen;

        lock (_replaceLock)
        {
            gen = _generators[game.GameId];
            if (gen.SettingsFingerprint == fingerprint) return gen;

            var next = GameGenerator.Create(game);
            lock (gen)
            {
                var revealed = new RevealedSeed(gen.RevealServerSeed(), gen.ServerSeedHash, DateTime.UtcNow);
                gen.Retired = true;
                next.Previous = revealed;
                logger.LogInformation("RNG settings changed — seed revealed and generator rebuilt: game={GameId} revealedSeed={Seed} hash={Hash}",
                    game.GameId, revealed.ServerSeed, revealed.ServerSeedHash);
            }
            _generators[game.GameId] = next;
            return next;
        }
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
        public RngSettings? Settings { get; private init; }
        public string SettingsFingerprint { get; private init; } = string.Empty;
        public bool Retired { get; set; }

        public static string Fingerprint(RngSettings? rng) => JsonSerializer.Serialize(rng, GameCatalogue.Json);

        public static GameGenerator Create(GameDefinition game)
        {
            var gen = CreateFor(game);
            return new GameGenerator
            {
                Next = gen.Next,
                RevealServerSeed = gen.RevealServerSeed,
                RotateServerSeed = gen.RotateServerSeed,
                GetServerSeedHash = gen.GetServerSeedHash,
                Settings = game.Rng,
                SettingsFingerprint = Fingerprint(game.Rng),
            };
        }

        private static GameGenerator CreateFor(GameDefinition game)
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
                    // Wheel: every pocket weight 1, so draw = pocket number with equal odds.
                    var weights = rng.Model switch
                    {
                        RngModel.Pick  => rng.Options.Select(o => o.Weight),
                        RngModel.Wheel => Enumerable.Repeat(1UL, rng.Pockets),
                        _              => rng.Outcomes.Select(o => o.Weight),
                    };
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
