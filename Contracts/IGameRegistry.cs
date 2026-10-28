using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>The games this API can launch and hold wallet sessions for.</summary>
public interface IGameRegistry
{
    IReadOnlyList<GameDefinition> All { get; }

    /// <summary>Returns the enabled game with this id, or null.</summary>
    GameDefinition? Find(string gameId);

    /// <summary>Returns the enabled game whose ServerKey matches, or null. Constant-time compare.</summary>
    GameDefinition? FindByServerKey(string serverKey);

    /// <summary>Returns the enabled game with this platform ProviderGameID, or null.</summary>
    GameDefinition? FindByProviderGameId(long providerGameId);
}
