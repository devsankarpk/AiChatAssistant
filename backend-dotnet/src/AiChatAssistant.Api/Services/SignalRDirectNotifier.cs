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
