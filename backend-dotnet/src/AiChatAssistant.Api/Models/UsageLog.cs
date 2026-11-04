namespace AiChatAssistant.Api.Models;

public class UsageLog
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public Guid SessionId { get; set; }

    public int TokensUsed { get; set; }

    /// <summary>The share of <see cref="TokensUsed"/> that was the question and history (0 when the AI service did not report a split).</summary>
    public int PromptTokens { get; set; }

    /// <summary>The share that was the answer (0 when not reported).</summary>
    public int CompletionTokens { get; set; }

    /// <summary>The model that answered, as the AI service reported it. Empty for rows written before cost tracking existed ("not tracked").</summary>
    public string Model { get; set; } = string.Empty;

    public decimal CostEstimate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;

    public ChatSession Session { get; set; } = null!;
}
