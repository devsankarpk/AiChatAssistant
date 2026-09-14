namespace AiChatAssistant.Api.Dtos.Chat;

public class CreateSessionRequest
{
    public string? Title { get; set; }
}

public class SessionResponse
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public class MessageResponse
{
    public int Id { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public class SendMessageRequest
{
    public string Content { get; set; } = string.Empty;
}

/// <summary>`.NET -> Angular` send-message response shape, per CLAUDE.md's cross-service contracts.</summary>
public class SendMessageResponse
{
    public MessageResponse UserMessage { get; set; } = null!;

    public MessageResponse AssistantMessage { get; set; } = null!;
}
