namespace AiChatAssistant.Api.Services;

/// <summary>
/// Process-local presence. It is correct only while the API runs as a single replica, which is why
/// CLAUDE.md pins the api to 1 replica until Phase 16 moves this (and the SignalR backplane) to Redis.
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
}
