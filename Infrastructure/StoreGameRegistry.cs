using System.Security.Cryptography;
using System.Text;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Game catalogue from the back-office config store (dbo.Games, seeded from appsettings "Games").
/// Every lookup reads the store's current snapshot, so back-office edits apply without a restart.
/// </summary>
public class StoreGameRegistry(IGameConfigStore store) : IGameRegistry
{
    public IReadOnlyList<GameDefinition> All => store.Games;

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
