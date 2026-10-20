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
}
