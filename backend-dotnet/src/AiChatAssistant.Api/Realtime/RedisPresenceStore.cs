using StackExchange.Redis;

namespace AiChatAssistant.Api.Realtime;

/// <summary>
/// Presence in Redis: one sorted set per user, <c>presence:user:{id}</c>, whose members are connection ids
/// and whose scores are the moment each connection's mark lapses (unix ms). A user is online while the set
/// holds a member that has not lapsed. The come-online / go-offline decisions run as Lua scripts so two
/// replicas handling a user's first two tabs at the same instant cannot both (or neither) report "came online".
/// </summary>
public sealed class RedisPresenceStore : IPresenceStore
{
    // KEYS[1] = set, ARGV = now, lapse score, connection id, key lifetime ms. Returns 1 when the set was empty.
    private const string AddScript = @"
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
local before = redis.call('ZCARD', KEYS[1])
redis.call('ZADD', KEYS[1], ARGV[2], ARGV[3])
redis.call('PEXPIRE', KEYS[1], ARGV[4])
if before == 0 then return 1 else return 0 end";

    // ARGV = now, connection id. Returns 1 only when this connection was present and nothing live is left.
    private const string RemoveScript = @"
local removed = redis.call('ZREM', KEYS[1], ARGV[2])
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
local remaining = redis.call('ZCARD', KEYS[1])
if remaining == 0 then
  redis.call('DEL', KEYS[1])
  if removed == 1 then return 1 end
end
return 0";

    private readonly IConnectionMultiplexer _redis;

    public RedisPresenceStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public static RedisKey KeyFor(int userId) => $"presence:user:{userId}";

    public async Task<bool> AddAsync(int userId, string connectionId, DateTimeOffset now, TimeSpan ttl)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            AddScript,
            new[] { KeyFor(userId) },
            new RedisValue[] { now.ToUnixTimeMilliseconds(), now.Add(ttl).ToUnixTimeMilliseconds(), connectionId, (long)(ttl.TotalMilliseconds * 3) });
        return (int)result == 1;
    }

    public async Task<bool> RemoveAsync(int userId, string connectionId, DateTimeOffset now)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            RemoveScript,
            new[] { KeyFor(userId) },
            new RedisValue[] { now.ToUnixTimeMilliseconds(), connectionId });
        return (int)result == 1;
    }

    public async Task<IReadOnlySet<int>> OnlineAmongAsync(IReadOnlyCollection<int> userIds, DateTimeOffset now)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<int>();
        }

        var db = _redis.GetDatabase();
        var batch = db.CreateBatch();
        var nowMs = now.ToUnixTimeMilliseconds();
        var counts = userIds.Distinct().ToDictionary(id => id, id => batch.SortedSetLengthAsync(KeyFor(id), nowMs, double.PositiveInfinity));
        batch.Execute();
        await Task.WhenAll(counts.Values);
        return counts.Where(kv => kv.Value.Result > 0).Select(kv => kv.Key).ToHashSet();
    }

    public async Task RefreshAsync(IReadOnlyList<(int UserId, string ConnectionId)> connections, DateTimeOffset now, TimeSpan ttl)
    {
        if (connections.Count == 0)
        {
            return;
        }

        var batch = _redis.GetDatabase().CreateBatch();
        var lapse = now.Add(ttl).ToUnixTimeMilliseconds();
        var tasks = new List<Task>();
        foreach (var (userId, connectionId) in connections)
        {
            tasks.Add(batch.SortedSetAddAsync(KeyFor(userId), connectionId, lapse));
        }
        foreach (var userId in connections.Select(c => c.UserId).Distinct())
        {
            tasks.Add(batch.KeyExpireAsync(KeyFor(userId), ttl * 3));
        }
        batch.Execute();
        await Task.WhenAll(tasks);
    }
}
