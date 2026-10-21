using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Hubs;

/// <summary>
/// The realtime channel for direct chat, at <c>/hubs/direct</c>. Browsers cannot set an Authorization
/// header on a WebSocket, so the JWT arrives as the <c>access_token</c> query string
/// (see <c>JwtBearerEvents.OnMessageReceived</c> in Program.cs). This hub only receives pushes: sending
/// and marking read stay on REST so validation and error handling live in one place.
/// </summary>
[Authorize]
public class DirectHub : Hub
{
    private readonly IPresenceTracker _presence;
    private readonly IDirectDeliveryService _delivery;
    private readonly IDirectNotifier _notifier;
    private readonly AppDbContext _db;

    public DirectHub(IPresenceTracker presence, IDirectDeliveryService delivery, IDirectNotifier notifier, AppDbContext db)
    {
        _presence = presence;
        _delivery = delivery;
        _notifier = notifier;
        _db = db;
    }

    /// <summary>Every connection of a user joins their personal group; pushes are addressed to it.</summary>
    public static string GroupFor(int userId) => $"user-{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.GetUserId() ?? 0;
        if (userId == 0)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId));
        // Online first, then deliver what was waiting: a message sent in between sees the user as
        // online and is marked delivered by the sender, and the pending query skips it.
        var cameOnline = _presence.UserConnected(userId, Context.ConnectionId);
        await _delivery.MarkPendingDeliveredAsync(userId);
        if (cameOnline)
        {
            await BroadcastPresenceAsync(userId, online: true, lastSeenAt: null); // a second tab changes nothing
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.GetUserId() ?? 0;
        if (userId != 0 && _presence.UserDisconnected(userId, Context.ConnectionId))
        {
            // Only when the last tab closes: that moment is the user's "last seen".
            var lastSeen = DateTime.UtcNow;
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is not null)
            {
                user.LastSeenAt = lastSeen;
                await _db.SaveChangesAsync();
            }
            await BroadcastPresenceAsync(userId, online: false, lastSeenAt: lastSeen);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client to server: "I am typing in this conversation". Forwarded to the other participant only, and
    /// only if the caller really is one of the two; anything else is silently ignored (no error, so a
    /// probe learns nothing about which conversation ids exist). The client sends this at most every few
    /// seconds while the draft is non-empty.
    /// </summary>
    public async Task Typing(Guid conversationId)
    {
        var userId = Context.User?.GetUserId() ?? 0;
        if (userId == 0)
        {
            return;
        }

        var conversation = await _db.DirectConversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && (c.UserLowId == userId || c.UserHighId == userId));
        if (conversation is null)
        {
            return;
        }

        var recipientId = conversation.UserLowId == userId ? conversation.UserHighId : conversation.UserLowId;
        await _notifier.PeerTypingAsync(recipientId, conversationId, userId);
    }

    /// <summary>Tell everyone who shares a conversation with this user that they came online or went offline.</summary>
    private async Task BroadcastPresenceAsync(int userId, bool online, DateTime? lastSeenAt)
    {
        var peerIds = await _db.DirectConversations.AsNoTracking()
            .Where(c => c.UserLowId == userId || c.UserHighId == userId)
            .Select(c => c.UserLowId == userId ? c.UserHighId : c.UserLowId)
            .ToListAsync();
        await _notifier.PeerPresenceChangedAsync(peerIds, userId, online, lastSeenAt);
    }
}
