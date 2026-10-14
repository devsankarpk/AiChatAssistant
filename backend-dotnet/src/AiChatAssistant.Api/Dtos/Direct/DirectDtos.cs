namespace AiChatAssistant.Api.Dtos.Direct;

/// <summary>A user as shown in the people search: name only. Never an email, never anything private.</summary>
public class UserSearchResult
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class StartConversationRequest
{
    public int UserId { get; set; }
}

public class SendDirectMessageRequest
{
    public string Content { get; set; } = string.Empty;
}

public class PeerResponse
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>`.NET -> Angular` direct message. <c>Status</c> is "sent", "delivered" or "read".</summary>
public class DirectMessageResponse
{
    public long Id { get; set; }

    public Guid ConversationId { get; set; }

    public int SenderId { get; set; }

    /// <summary>Empty once the message is deleted (the text is cleared on the server).</summary>
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string Status { get; set; } = "sent";

    public bool Deleted { get; set; }
}

public class ConversationResponse
{
    public Guid Id { get; set; }

    public PeerResponse Peer { get; set; } = null!;

    public DirectMessageResponse? LastMessage { get; set; }

    public int UnreadCount { get; set; }

    /// <summary>Always false until live presence exists (Phase 11).</summary>
    public bool PeerOnline { get; set; }

    /// <summary>Null until live presence exists (Phase 11).</summary>
    public DateTime? PeerLastSeenAt { get; set; }
}
