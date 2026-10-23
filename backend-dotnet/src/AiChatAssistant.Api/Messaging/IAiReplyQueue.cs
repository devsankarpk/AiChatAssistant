namespace AiChatAssistant.Api.Messaging;

/// <summary>Hands a job to the AI reply workers. Throws if the broker did not accept it, so the caller never believes a job is queued when it is not.</summary>
public interface IAiReplyQueue
{
    Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken);
}
