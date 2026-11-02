namespace AiChatAssistant.Api.Realtime;

/// <summary>
/// The shared record of live connections (Redis in production). Kept separate from
/// <c>RedisPresenceTracker</c> so the tracker's fallback behaviour can be tested with a fake store.
/// Every call takes "now" so expiry is decided by the caller's clock, not by hidden state.
/// </summary>
public interface IPresenceStore
{
    /// <returns>true when the user had no live connection anywhere before this one.</returns>
    Task<bool> AddAsync(int userId, string connectionId, DateTimeOffset now, TimeSpan ttl);

    /// <returns>true when that removed the user's last live connection anywhere.</returns>
    Task<bool> RemoveAsync(int userId, string connectionId, DateTimeOffset now);

    Task<IReadOnlySet<int>> OnlineAmongAsync(IReadOnlyCollection<int> userIds, DateTimeOffset now);

    /// <summary>
    /// People none of whose connection marks have been refreshed for <paramref name="grace"/> past their lapse: their replica died. Each
    /// is removed here and returned once, with the moment of their last heartbeat; <paramref name="skip"/> protects people this
    /// process still holds live connections for.
    /// </summary>
    Task<IReadOnlyList<(int UserId, DateTimeOffset LastBeat)>> ExpireLapsedAsync(DateTimeOffset now, TimeSpan grace, TimeSpan ttl, Func<int, bool> skip, int max);

    /// <summary>Extend the lifetime of these connections (and recreate them if the store lost them).</summary>
    Task RefreshAsync(IReadOnlyList<(int UserId, string ConnectionId)> connections, DateTimeOffset now, TimeSpan ttl);
}
