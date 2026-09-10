namespace AiChatAssistant.Api.Models;

public class Message
{
    public int Id { get; set; }

    public Guid SessionId { get; set; }

    public MessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public ChatSession Session { get; set; } = null!;
}
