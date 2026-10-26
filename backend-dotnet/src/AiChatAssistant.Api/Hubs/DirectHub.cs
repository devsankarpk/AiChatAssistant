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
    private readonly PresenceAnnouncer _announcer;
    private readonly AppDbContext _db;
    private readonly ILogger<DirectHub> _logger;

    public DirectHub(IPresenceTracker presence, IDirectDeliveryService delivery, IDirectNotifier notifier, PresenceAnnouncer announcer, AppDbContext db, ILogger<DirectHub> logger)
    {
        _announcer = announcer;
        _logger = logger;
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
        var cameOnline = await _presence.UserConnectedAsync(userId, Context.ConnectionId);
        // Presence problems ("why does Bob still show as online?") are only diagnosable with these two lines.
        _logger.LogInformation("Hub connected: user {UserId}, connection {ConnectionId}, cameOnline={CameOnline}", userId, Context.ConnectionId, cameOnline);
        await _delivery.MarkPendingDeliveredAsync(userId);
        if (cameOnline)
        {
            await _announcer.AnnounceOnlineAsync(userId); // a second tab changes nothing
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.GetUserId() ?? 0;
        var wentOffline = userId != 0 && await _presence.UserDisconnectedAsync(userId, Context.ConnectionId);
        _logger.LogInformation("Hub disconnected: user {UserId}, connection {ConnectionId}, wentOffline={WentOffline}, reason={Reason}", userId, Context.ConnectionId, wentOffline, exception?.Message ?? "closed normally");
        if (wentOffline)
        {
            // Only when the last tab closes: that moment is the user's "last seen".
            await _announcer.AnnounceOfflineAsync(userId, DateTime.UtcNow);
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
}
