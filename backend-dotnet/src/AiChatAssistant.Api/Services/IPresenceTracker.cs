namespace AiChatAssistant.Api.Services;

/// <summary>A person whose every connection lapsed without a clean disconnect (their API replica died). <paramref name="LastSeenUtc"/> is their last heartbeat.</summary>
public sealed record LapsedPresence(int UserId, DateTime LastSeenUtc);

/// <summary>
/// Who has at least one live realtime connection right now. A user can have several (one per
/// browser tab, and with several API replicas on different replicas), so "online" means "one or more
/// connections" and "offline" means the last one closed. The seam exists so controllers and tests never
/// need a real hub or a real Redis. Async because the shared implementation talks to Redis.
/// </summary>
public interface IPresenceTracker
{
    Task<bool> IsOnlineAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Which of these users are online right now (one round trip, for the conversation list).</summary>
    Task<IReadOnlySet<int>> OnlineAmongAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default);

    /// <returns>true when this is the user's first connection anywhere, i.e. they just came online.</returns>
    Task<bool> UserConnectedAsync(int userId, string connectionId);

    /// <returns>true when that was the user's last connection anywhere, i.e. they just went offline.</returns>
    Task<bool> UserDisconnectedAsync(int userId, string connectionId);

    /// <summary>
    /// Called on a timer. A shared tracker marks this process's connections as still alive; if the
    /// process dies the marks lapse and its users drop offline by themselves. Nothing to do in memory.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// People who are now offline because their replica crashed: nobody ran their disconnect, so nobody announced it. Each person is
    /// returned to exactly one caller, across all replicas. Empty when there is nothing to do (always, in memory).
    /// </summary>
    Task<IReadOnlyList<LapsedPresence>> SweepLapsedAsync(CancellationToken cancellationToken = default);
}
