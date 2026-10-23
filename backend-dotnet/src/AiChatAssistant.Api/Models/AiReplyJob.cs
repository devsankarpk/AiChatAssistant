namespace AiChatAssistant.Api.Models;

public enum AiReplyJobStatus
{
    /// <summary>Waiting for a worker (new, or put back after a failed attempt that will be retried).</summary>
    Pending,

    /// <summary>A worker has claimed it and is calling the AI service. The claim lapses after a lease, see <see cref="AiReplyJob.ClaimedAt"/>.</summary>
    Processing,

    Done,

    /// <summary>All attempts failed. The person can ask for another go (POST /api/chat/jobs/{id}/retry).</summary>
    Failed,
}

/// <summary>
/// One request for an AI reply when <c>Chat:AsyncReplies</c> is on (Phase 18). The row is what makes the work
/// safe to repeat: a message redelivered by the broker finds the job already done and does nothing, and a page
/// reload can show a reply that is still pending or has failed. Only the job id travels through RabbitMQ.
/// </summary>
public class AiReplyJob
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public int UserId { get; set; }

    /// <summary>The person's message this job answers.</summary>
    public int UserMessageId { get; set; }

    public AiReplyJobStatus Status { get; set; } = AiReplyJobStatus.Pending;

    /// <summary>How many times a worker has started on this job (including the current one).</summary>
    public int Attempts { get; set; }

    /// <summary>Short reason for the last failure; never contains message text.</summary>
    public string? Error { get; set; }

    /// <summary>The reply, once <see cref="Status"/> is Done.</summary>
    public int? AssistantMessageId { get; set; }

    /// <summary>When the current worker claimed the job. A claim older than the lease is treated as abandoned (worker died).</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// Changes on every claim and is a concurrency token, so two workers racing for the same job cannot both
    /// win: the second save fails and that worker backs off.
    /// </summary>
    public Guid ClaimToken { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ChatSession Session { get; set; } = null!;

    public User User { get; set; } = null!;

    public Message UserMessage { get; set; } = null!;

    public Message? AssistantMessage { get; set; }
}
