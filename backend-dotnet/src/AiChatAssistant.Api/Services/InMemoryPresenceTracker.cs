namespace AiChatAssistant.Api.Services;

/// <summary>
/// Process-local presence. Correct only for a single API replica, so it is used when Redis is not
/// configured (local development without Docker). With Redis, <c>RedisPresenceTracker</c> shares presence
/// across replicas and keeps one of these as its per-process record of its own connections.
/// </summary>
public class InMemoryPresenceTracker : IPresenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<int, HashSet<string>> _connections = new();

    public bool IsOnline(int userId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(userId, out var set) && set.Count > 0;
        }
    }

    public bool UserConnected(int userId, string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                set = new HashSet<string>();
                _connections[userId] = set;
            }

            var wasOffline = set.Count == 0;
            set.Add(connectionId);
            return wasOffline;
        }
    }

    public bool UserDisconnected(int userId, string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(userId, out var set) || !set.Remove(connectionId))
            {
                return false; // never seen this connection (e.g. the server restarted): nothing changed
            }

            if (set.Count > 0)
            {
                return false; // another tab is still connected
            }

            _connections.Remove(userId);
            return true;
        }
    }

    /// <summary>Every (user, connection) this process holds; the Redis tracker refreshes these on a timer.</summary>
    public IReadOnlyList<(int UserId, string ConnectionId)> Snapshot()
    {
        lock (_gate)
        {
            return _connections.SelectMany(kv => kv.Value.Select(c => (kv.Key, c))).ToList();
        }
    }

    public Task<bool> IsOnlineAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsOnline(userId));

    public Task<IReadOnlySet<int>> OnlineAmongAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<int>>(userIds.Where(IsOnline).ToHashSet());

    public Task<bool> UserConnectedAsync(int userId, string connectionId) =>
        Task.FromResult(UserConnected(userId, connectionId));

    public Task<bool> UserDisconnectedAsync(int userId, string connectionId) =>
        Task.FromResult(UserDisconnected(userId, connectionId));

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<LapsedPresence>> SweepLapsedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LapsedPresence>>(Array.Empty<LapsedPresence>());
}
