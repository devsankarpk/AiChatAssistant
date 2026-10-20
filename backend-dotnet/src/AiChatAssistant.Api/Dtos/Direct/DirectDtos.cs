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

    /// <summary>True while the peer has at least one live connection (from the in-memory presence tracker).</summary>
    public bool PeerOnline { get; set; }

    /// <summary>When the peer's last connection closed; null if they have never connected since Phase 11.</summary>
    public DateTime? PeerLastSeenAt { get; set; }
}

/// <summary>Result of marking a conversation read.</summary>
public class MarkReadResponse
{
    public Guid ConversationId { get; set; }

    /// <summary>How many of the peer's messages were newly marked read (0 when nothing was unread).</summary>
    public int ReadCount { get; set; }

    /// <summary>The newest message that was marked read; null when ReadCount is 0.</summary>
    public long? UpToMessageId { get; set; }
}
