using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>
/// Chat message storage and retrieval.
/// Decouples the hub from message creation — the hub receives a fully-formed
/// ChatMessage back and can broadcast it without touching domain internals.
/// </summary>
public interface IChatService
{
    /// <summary>Validate, store, and return a new chat message. Returns null if the message is invalid.</summary>
    ChatMessage? Add(string playerId, string username, string text);

    /// <summary>Return the most recent messages for new connections.</summary>
    IReadOnlyList<ChatMessage> GetRecent(int count = 50);
}
