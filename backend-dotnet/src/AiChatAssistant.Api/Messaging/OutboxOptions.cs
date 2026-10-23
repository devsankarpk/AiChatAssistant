namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// The transactional outbox for direct-chat events (config section "Outbox", Phase 19). Active only when
/// RabbitMQ is configured and <see cref="Enabled"/> is true; otherwise no rows are written at all.
/// </summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the dispatcher looks for new events. The delay between a change and its event is at most this.</summary>
    public int PollMilliseconds { get; set; } = 1000;

    /// <summary>Most events published per round.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>A claim older than this is treated as abandoned (the replica that took it died) and the event is taken again.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>Published events are kept this long, for inspection, then deleted.</summary>
    public int RetentionHours { get; set; } = 24;
}
