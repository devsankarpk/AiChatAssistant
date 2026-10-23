using System.Diagnostics.Metrics;
using System.Text.Json;

namespace AiChatAssistant.Api.Messaging;

public enum DirectEventResult
{
    Handled,
    Duplicate,
    Invalid,
}

/// <summary>
/// The version-1 subscriber of <c>direct.events</c>: it logs each event and counts it (meter
/// <c>AiChatAssistant.DirectEvents</c>, counter <c>aichat.direct_events</c> tagged by type), which proves the
/// fan-out end to end and gives later phases (metrics, Phase 27) something to expose. It ignores a repeat of an
/// event it has already seen (same outbox id), since delivery is at-least-once. Kept free of RabbitMQ for testing.
/// </summary>
public sealed class DirectEventHandler
{
    public const string MeterName = "AiChatAssistant.DirectEvents";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("aichat.direct_events", description: "Direct-chat events consumed, by type");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("aichat.direct_events_duplicate", description: "Repeated deliveries ignored");

    private readonly IEventDeduplicator _dedupe;
    private readonly ILogger<DirectEventHandler> _logger;

    public DirectEventHandler(IEventDeduplicator dedupe, ILogger<DirectEventHandler> logger)
    {
        _dedupe = dedupe;
        _logger = logger;
    }

    public async Task<DirectEventResult> HandleAsync(string? type, string? messageId, ReadOnlyMemory<byte> body)
    {
        DirectEventPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DirectEventPayload>(body.Span, DirectEvents.Json);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(messageId) || payload is null || payload.ConversationId == Guid.Empty || payload.MessageIds.Count == 0)
        {
            _logger.LogWarning("Direct event rejected: missing type, id or payload");
            return DirectEventResult.Invalid;
        }

        if (!await _dedupe.TryMarkSeenAsync(messageId))
        {
            Duplicates.Add(1, new KeyValuePair<string, object?>("type", type));
            _logger.LogInformation("Direct event {Type} #{OutboxId} seen before; ignoring the repeat", type, messageId);
            return DirectEventResult.Duplicate;
        }

        Events.Add(1, new KeyValuePair<string, object?>("type", type));
        // Ids and status only: the event never had message text in it.
        _logger.LogInformation(
            "Direct event {Type} #{OutboxId}: conversation {ConversationId}, author {SenderId} to {RecipientId}, {Count} message(s) {Status}",
            type, messageId, payload.ConversationId, payload.SenderId, payload.RecipientId, payload.MessageIds.Count, payload.Status);
        return DirectEventResult.Handled;
    }
}
