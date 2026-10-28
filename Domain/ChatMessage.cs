namespace Worldiator.Server.Domain;

public class ChatMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string PlayerId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
