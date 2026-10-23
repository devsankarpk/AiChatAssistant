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

/// <summary>
/// The send-message response when <c>Chat:AsyncReplies</c> is on (Phase 18): <c>202</c>, the question is saved, the
/// reply is being produced and arrives as the SignalR event <c>AssistantReplied</c> (or <c>AssistantFailed</c>).
/// </summary>
public class SendMessageAcceptedResponse
{
    public MessageResponse UserMessage { get; set; } = null!;

    /// <summary>Always "pending" here.</summary>
    public string ReplyStatus { get; set; } = "pending";

    public Guid JobId { get; set; }
}

/// <summary>A reply that is not delivered yet: still being produced ("pending") or given up on ("failed").</summary>
public class ReplyJobResponse
{
    public Guid JobId { get; set; }

    public int UserMessageId { get; set; }

    public string Status { get; set; } = "pending";

    /// <summary>Only for "failed": a message that is safe to show.</summary>
    public string? Error { get; set; }
}

public class RetryReplyResponse
{
    public Guid JobId { get; set; }

    public string ReplyStatus { get; set; } = "pending";
}
