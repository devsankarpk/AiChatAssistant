using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AiChatAssistant.Api.Services;

/// <summary>Sends the async-reply events on the same hub and the same per-user group as the direct-chat events.</summary>
public class SignalRChatNotifier : IChatNotifier
{
    private readonly IHubContext<DirectHub> _hub;
    private readonly ILogger<SignalRChatNotifier> _logger;

    public SignalRChatNotifier(IHubContext<DirectHub> hub, ILogger<SignalRChatNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public Task AssistantRepliedAsync(int userId, Guid jobId, Guid sessionId, int userMessageId, MessageResponse assistantMessage, CancellationToken cancellationToken = default) =>
        SendAsync(userId, "AssistantReplied", new { jobId, sessionId, userMessageId, assistantMessage }, cancellationToken);

    public Task AssistantFailedAsync(int userId, Guid jobId, Guid sessionId, int userMessageId, string code, string message, CancellationToken cancellationToken = default) =>
        SendAsync(userId, "AssistantFailed", new { jobId, sessionId, userMessageId, error = new { code, message } }, cancellationToken);

    private async Task SendAsync(int userId, string method, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.Group(DirectHub.GroupFor(userId)).SendAsync(method, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not push {Method} to user {UserId}", method, userId);
        }
    }
}
