using System.Security.Cryptography;
using System.Text;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>Game catalogue bound from the "Games" array in appsettings.json.</summary>
public class ConfigGameRegistry : IGameRegistry
{
    public IReadOnlyList<GameDefinition> All { get; }

    public ConfigGameRegistry(IConfiguration config)
    {
        All = config.GetSection("Games").Get<List<GameDefinition>>() ?? [];

        if (All.Any(g => string.IsNullOrWhiteSpace(g.GameId)))
            throw new InvalidOperationException("Games: every entry needs a GameId.");
        var duplicate = All.GroupBy(g => g.GameId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Games: duplicate GameId \"{duplicate.Key}\".");

        foreach (var game in All)
            game.Validate();

        var sharedId = All.Where(g => g.ProviderGameId != null).GroupBy(g => g.ProviderGameId).FirstOrDefault(g => g.Count() > 1);
        if (sharedId != null)
            throw new InvalidOperationException($"Games: ProviderGameId {sharedId.Key} is used by more than one game.");
    }

    public GameDefinition? Find(string gameId) =>
        All.FirstOrDefault(g => g.Enabled && string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    public GameDefinition? FindByProviderGameId(long providerGameId) =>
        All.FirstOrDefault(g => g.Enabled && g.ProviderGameId == providerGameId);

    public GameDefinition? FindByServerKey(string serverKey)
    {
        if (string.IsNullOrEmpty(serverKey)) return null;
        var incoming = Encoding.UTF8.GetBytes(serverKey);
        return All.FirstOrDefault(g => g.Enabled && !string.IsNullOrEmpty(g.ServerKey)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(g.ServerKey), incoming));
    }
}
