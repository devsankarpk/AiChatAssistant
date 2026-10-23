using AiChatAssistant.Api.Dtos.Chat;

namespace AiChatAssistant.Api.Services;

/// <summary>
/// Pushes the outcome of an async AI reply to the person's browser (Phase 18). Best effort, like
/// <see cref="IDirectNotifier"/>: the database is the source of truth and the page can always refetch,
/// so an implementation must never throw into the worker.
/// </summary>
public interface IChatNotifier
{
    /// <summary>"AssistantReplied": the reply is ready and saved.</summary>
    Task AssistantRepliedAsync(int userId, Guid jobId, Guid sessionId, int userMessageId, MessageResponse assistantMessage, CancellationToken cancellationToken = default);

    /// <summary>"AssistantFailed": every attempt failed; the person can ask for another go.</summary>
    Task AssistantFailedAsync(int userId, Guid jobId, Guid sessionId, int userMessageId, string code, string message, CancellationToken cancellationToken = default);
}
