using AiChatAssistant.Api.Dtos.Direct;

namespace AiChatAssistant.Api.Services;

/// <summary>
/// Pushes direct-chat events to connected browsers. The database is always the source of truth; these
/// pushes are best effort, so an implementation must never throw into the request that triggered it.
/// Controllers depend on this interface (not on the hub) so tests need no SignalR.
/// </summary>
public interface IDirectNotifier
{
    /// <summary>"MessageReceived" to the recipient: a new message arrived.</summary>
    Task MessageReceivedAsync(int recipientId, DirectMessageResponse message, CancellationToken cancellationToken = default);

    /// <summary>"MessageStatusChanged" to the sender: these messages are now "delivered" or "read".</summary>
    Task MessagesStatusChangedAsync(int senderId, Guid conversationId, IReadOnlyList<long> messageIds, string status, CancellationToken cancellationToken = default);

    /// <summary>"MessagesRead" to the reader's own connections, so their other tabs clear the unread count.</summary>
    Task MessagesReadAsync(int readerId, Guid conversationId, long upToMessageId, CancellationToken cancellationToken = default);

    /// <summary>"MessageDeleted" to one participant (called for both): this message no longer has any text.</summary>
    Task MessageDeletedAsync(int userId, Guid conversationId, long messageId, CancellationToken cancellationToken = default);

    /// <summary>"PeerTyping" to the other participant only: <paramref name="typingUserId"/> is typing in this conversation.</summary>
    Task PeerTypingAsync(int recipientId, Guid conversationId, int typingUserId, CancellationToken cancellationToken = default);

    /// <summary>"PeerPresenceChanged" to everyone who shares a conversation with <paramref name="userId"/>.</summary>
    Task PeerPresenceChangedAsync(IReadOnlyCollection<int> peerIds, int userId, bool online, DateTime? lastSeenAt, CancellationToken cancellationToken = default);
}
