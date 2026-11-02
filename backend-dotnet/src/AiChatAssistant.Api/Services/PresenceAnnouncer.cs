using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

/// <summary>
/// What happens when a person has gone offline: their "last seen" is saved and everyone who shares a conversation with them is told.
/// One place for both ways it can happen: the last tab closing (<c>DirectHub</c>) and a crashed replica (<c>PresenceSweeper</c>).
/// </summary>
public class PresenceAnnouncer
{
    private readonly AppDbContext _db;
    private readonly IDirectNotifier _notifier;

    public PresenceAnnouncer(AppDbContext db, IDirectNotifier notifier)
    {
        _db = db;
        _notifier = notifier;
    }

    public async Task AnnounceOfflineAsync(int userId, DateTime lastSeenUtc)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is not null)
        {
            user.LastSeenAt = lastSeenUtc;
            await _db.SaveChangesAsync();
        }

        await BroadcastAsync(userId, online: false, lastSeenUtc);
    }

    public async Task AnnounceOnlineAsync(int userId) => await BroadcastAsync(userId, online: true, lastSeenAt: null);

    private async Task BroadcastAsync(int userId, bool online, DateTime? lastSeenAt)
    {
        var peerIds = await _db.DirectConversations.AsNoTracking()
            .Where(c => c.UserLowId == userId || c.UserHighId == userId)
            .Select(c => c.UserLowId == userId ? c.UserHighId : c.UserLowId)
            .ToListAsync();
        await _notifier.PeerPresenceChangedAsync(peerIds, userId, online, lastSeenAt);
    }
}
