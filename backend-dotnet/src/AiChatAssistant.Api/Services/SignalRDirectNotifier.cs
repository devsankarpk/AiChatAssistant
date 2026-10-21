using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AiChatAssistant.Api.Services;

public class SignalRDirectNotifier : IDirectNotifier
{
    private readonly IHubContext<DirectHub> _hub;
    private readonly ILogger<SignalRDirectNotifier> _logger;

    public SignalRDirectNotifier(IHubContext<DirectHub> hub, ILogger<SignalRDirectNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public Task MessageReceivedAsync(int recipientId, DirectMessageResponse message, CancellationToken cancellationToken = default) =>
        SendAsync(recipientId, "MessageReceived", message, cancellationToken);

    public Task MessagesStatusChangedAsync(int senderId, Guid conversationId, IReadOnlyList<long> messageIds, string status, CancellationToken cancellationToken = default) =>
        SendAsync(senderId, "MessageStatusChanged", new { conversationId, messageIds, status }, cancellationToken);

    public Task MessagesReadAsync(int readerId, Guid conversationId, long upToMessageId, CancellationToken cancellationToken = default) =>
        SendAsync(readerId, "MessagesRead", new { conversationId, upToMessageId }, cancellationToken);

    public Task MessageDeletedAsync(int userId, Guid conversationId, long messageId, CancellationToken cancellationToken = default) =>
        SendAsync(userId, "MessageDeleted", new { conversationId, messageId }, cancellationToken);

    public Task PeerTypingAsync(int recipientId, Guid conversationId, int typingUserId, CancellationToken cancellationToken = default) =>
        SendAsync(recipientId, "PeerTyping", new { conversationId, userId = typingUserId }, cancellationToken);

    public async Task PeerPresenceChangedAsync(IReadOnlyCollection<int> peerIds, int userId, bool online, DateTime? lastSeenAt, CancellationToken cancellationToken = default)
    {
        if (peerIds.Count == 0)
        {
            return;
        }

        try
        {
            await _hub.Clients.Groups(peerIds.Select(DirectHub.GroupFor).ToList())
                .SendAsync("PeerPresenceChanged", new { userId, online, lastSeenAt }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push PeerPresenceChanged for user {UserId}", userId);
        }
    }

    private async Task SendAsync(int userId, string method, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.Group(DirectHub.GroupFor(userId)).SendAsync(method, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            // The message is already saved; a failed push only means the other side sees it on their
            // next load. Never fail the request over it.
            _logger.LogWarning(ex, "Could not push {Method} to user {UserId}", method, userId);
        }
    }
}
