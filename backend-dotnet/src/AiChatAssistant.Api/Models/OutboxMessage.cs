namespace AiChatAssistant.Api.Models;

/// <summary>
/// An event waiting to be published to RabbitMQ (Phase 19). It is written in the SAME database transaction as
/// the change it describes, so an event is never lost (the change committed but nothing was queued) and never
/// invented (it was queued but the change rolled back). A background dispatcher publishes it afterwards.
/// The payload carries ids and status only, never message text.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }

    /// <summary>Event name, also the RabbitMQ routing key, e.g. <c>direct.message.sent</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON. Ids, status and times only.</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set once RabbitMQ has confirmed the publish. Null = still to do.</summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>When a dispatcher claimed it; a claim older than the lease is treated as abandoned.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Concurrency token: replicas racing for the same row cannot both claim it.</summary>
    public Guid ClaimToken { get; set; }

    /// <summary>Failed publish attempts so far.</summary>
    public int Attempts { get; set; }
}
