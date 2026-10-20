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
    private readonly AppDbContext _db;

    public DirectHub(IPresenceTracker presence, IDirectDeliveryService delivery, AppDbContext db)
    {
        _presence = presence;
        _delivery = delivery;
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
        _presence.UserConnected(userId, Context.ConnectionId);
        await _delivery.MarkPendingDeliveredAsync(userId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.GetUserId() ?? 0;
        if (userId != 0 && _presence.UserDisconnected(userId, Context.ConnectionId))
        {
            // Only when the last tab closes: that moment is the user's "last seen".
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is not null)
            {
                user.LastSeenAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
