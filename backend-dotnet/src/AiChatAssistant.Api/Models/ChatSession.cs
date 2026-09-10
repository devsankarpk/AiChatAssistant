namespace AiChatAssistant.Api.Models;

public class ChatSession
{
    /// <summary>Guid so it can be passed as <c>session_id</c> in the .NET → Python contract.</summary>
    public Guid Id { get; set; }

    public int UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;

    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public ICollection<UsageLog> UsageLogs { get; set; } = new List<UsageLog>();
}
