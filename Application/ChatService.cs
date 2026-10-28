using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

/// <summary>
/// Stores chat messages and enforces validation.
/// Previously, message creation and validation happened inline in GameHub.SendChat —
/// meaning new connections received no chat history and the hub held domain logic.
/// </summary>
public class ChatService : IChatService
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();

    public ChatMessage? Add(string playerId, string username, string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 200) return null;

        var msg = new ChatMessage
        {
            PlayerId = playerId,
            Username = username,
            Text = trimmed,
        };

        lock (_lock)
        {
            _messages.Add(msg);
            if (_messages.Count > 200) _messages.RemoveAt(0);
        }

        return msg;
    }

    public IReadOnlyList<ChatMessage> GetRecent(int count = 50)
    {
        lock (_lock) { return _messages.TakeLast(count).ToList(); }
    }
}
