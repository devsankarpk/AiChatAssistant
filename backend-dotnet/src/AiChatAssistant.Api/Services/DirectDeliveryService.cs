using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Messaging;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

public class DirectDeliveryService : IDirectDeliveryService
{
    private readonly AppDbContext _db;
    private readonly IDirectNotifier _notifier;
    private readonly IOutbox _outbox;

    public DirectDeliveryService(AppDbContext db, IDirectNotifier notifier, IOutbox? outbox = null)
    {
        _outbox = outbox ?? new NoOpOutbox();
        _db = db;
        _notifier = notifier;
    }

    public async Task MarkPendingDeliveredAsync(int userId, CancellationToken cancellationToken = default)
    {
        var pending = await _db.DirectMessages
            .Where(m => m.SenderId != userId
                && m.DeliveredAt == null
                && m.DeletedAt == null
                && (m.Conversation.UserLowId == userId || m.Conversation.UserHighId == userId))
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var message in pending)
        {
            message.DeliveredAt = now;
        }
        // One event per sender and conversation, saved with the delivery marks (ids only).
        foreach (var group in pending.GroupBy(m => new { m.SenderId, m.ConversationId }))
        {
            var ids = group.Select(m => m.Id).ToList();
            _outbox.Add(DirectEvents.Delivered, () => DirectEvents.Payload(group.Key.ConversationId, ids, group.Key.SenderId, userId, "delivered", now));
        }
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var group in pending.GroupBy(m => new { m.SenderId, m.ConversationId }))
        {
            await _notifier.MessagesStatusChangedAsync(
                group.Key.SenderId,
                group.Key.ConversationId,
                group.Select(m => m.Id).OrderBy(id => id).ToList(),
                "delivered",
                cancellationToken);
        }
    }
}
