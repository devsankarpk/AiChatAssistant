using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

public class DirectDeliveryService : IDirectDeliveryService
{
    private readonly AppDbContext _db;
    private readonly IDirectNotifier _notifier;

    public DirectDeliveryService(AppDbContext db, IDirectNotifier notifier)
    {
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
