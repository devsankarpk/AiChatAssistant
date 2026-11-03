namespace AiChatAssistant.Api.Models;

public class ChatSession
{
    /// <summary>Guid so it can be passed as <c>session_id</c> in the .NET → Python contract.</summary>
    public Guid Id { get; set; }

    public int UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the owner deletes the chat. The row stays (usage logs keep pointing at it) but the chat is hidden everywhere and answers 404.</summary>
    public DateTime? DeletedAt { get; set; }

    public User User { get; set; } = null!;

    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public ICollection<UsageLog> UsageLogs { get; set; } = new List<UsageLog>();
}
