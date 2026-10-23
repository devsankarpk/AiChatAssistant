using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Realtime;

/// <summary>
/// Presence shared by every API replica. Each process also keeps its own connections in memory, for two
/// reasons: the heartbeat needs to know which marks to refresh, and when Redis is unreachable the tracker
/// degrades to this process's own view (right for one replica, partial for several) instead of failing
/// the request or the hub connection. When Redis returns, the next heartbeat re-creates every mark.
/// </summary>
public sealed class RedisPresenceTracker : IPresenceTracker
{
    private readonly IPresenceStore _store;
    private readonly InMemoryPresenceTracker _local = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisPresenceTracker> _logger;

    public RedisPresenceTracker(IPresenceStore store, IOptions<PresenceOptions> options, TimeProvider time, ILogger<RedisPresenceTracker> logger)
    {
        _store = store;
        _time = time;
        _ttl = TimeSpan.FromSeconds(options.Value.TtlSeconds);
        _logger = logger;
    }

    public async Task<bool> IsOnlineAsync(int userId, CancellationToken cancellationToken = default) =>
        (await OnlineAmongAsync(new[] { userId }, cancellationToken)).Contains(userId);

    public async Task<IReadOnlySet<int>> OnlineAmongAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.OnlineAmongAsync(userIds, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shared presence is unavailable; using this server's own view");
            return userIds.Where(_local.IsOnline).ToHashSet();
        }
    }

    public async Task<bool> UserConnectedAsync(int userId, string connectionId)
    {
        var firstHere = _local.UserConnected(userId, connectionId);
        try
        {
            return await _store.AddAsync(userId, connectionId, _time.GetUtcNow(), _ttl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shared presence is unavailable; treating user {UserId} as online based on this server only", userId);
            return firstHere;
        }
    }

    public async Task<bool> UserDisconnectedAsync(int userId, string connectionId)
    {
        var lastHere = _local.UserDisconnected(userId, connectionId);
        try
        {
            return await _store.RemoveAsync(userId, connectionId, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shared presence is unavailable; treating user {UserId} as offline based on this server only", userId);
            return lastHere;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var mine = _local.Snapshot();
        if (mine.Count == 0)
        {
            return;
        }

        try
        {
            await _store.RefreshAsync(mine, _time.GetUtcNow(), _ttl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh presence for {Count} connection(s); will try again", mine.Count);
        }
    }
}
