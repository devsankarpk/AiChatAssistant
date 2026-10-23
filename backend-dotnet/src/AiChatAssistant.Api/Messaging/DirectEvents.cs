using System.Text.Json;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// The direct-chat events published to RabbitMQ (exchange <c>direct.events</c>, topic). They carry ids, status and a
/// time only. Message text must never appear in an event: a subscriber that needs it can read the database.
/// </summary>
public static class DirectEvents
{
    public const string Sent = "direct.message.sent";
    public const string Delivered = "direct.message.delivered";
    public const string Read = "direct.message.read";
    public const string Deleted = "direct.message.deleted";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DirectEventPayload Payload(Guid conversationId, IEnumerable<long> messageIds, int authorId, int otherParticipantId, string status, DateTime at) =>
        new(conversationId, messageIds.OrderBy(i => i).ToList(), authorId, otherParticipantId, status, at);
}

/// <param name="SenderId">Always the author of the message(s), whichever way the event points.</param>
/// <param name="RecipientId">The other participant.</param>
/// <param name="Status">sent, delivered, read or deleted.</param>
public sealed record DirectEventPayload(Guid ConversationId, IReadOnlyList<long> MessageIds, int SenderId, int RecipientId, string Status, DateTime At);
