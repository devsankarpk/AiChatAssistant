namespace AiChatAssistant.Api.Models;

public class UsageLog
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public Guid SessionId { get; set; }

    public int TokensUsed { get; set; }

    public decimal CostEstimate { get; set; }

    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;

    public ChatSession Session { get; set; } = null!;
}
