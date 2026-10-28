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
    /// <summary>
    /// Index of everyone who has a presence set: member = user id, score = the latest moment any of their marks lapses. It is how the
    /// sweeper finds people whose replica crashed without scanning every key. Written by the scripts below; never read to decide "online".
    /// </summary>
    public static readonly RedisKey IndexKey = "presence:index";

    // KEYS[1] = set, KEYS[2] = index. ARGV = now, lapse score, connection id, key lifetime ms, user id. Returns 1 when the set was empty.
    private const string AddScript = @"
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
local before = redis.call('ZCARD', KEYS[1])
redis.call('ZADD', KEYS[1], ARGV[2], ARGV[3])
redis.call('PEXPIRE', KEYS[1], ARGV[4])
redis.call('ZADD', KEYS[2], 'GT', ARGV[2], ARGV[5])
if before == 0 then return 1 else return 0 end";

    // ARGV = now, connection id, user id. Returns 1 only when this connection was present and nothing live is left.
    private const string RemoveScript = @"
local removed = redis.call('ZREM', KEYS[1], ARGV[2])
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
local remaining = redis.call('ZCARD', KEYS[1])
if remaining == 0 then
  redis.call('DEL', KEYS[1])
  redis.call('ZREM', KEYS[2], ARGV[3])
  if removed == 1 then return 1 end
end
return 0";

    // KEYS[1] = set, KEYS[2] = index. ARGV = cutoff (ms; marks at or before it are dead), user id, fallback last beat (the index score).
    // Returns the user's last heartbeat (ms) when this call took them offline, else 0 (someone refreshed a mark in the meantime, or another
    // replica got here first). Atomic, so exactly one caller can ever receive a non-zero answer for the same outage.
    private const string ExpireScript = @"
local top = redis.call('ZREVRANGE', KEYS[1], 0, 0, 'WITHSCORES')
local last = tonumber(ARGV[3])
if top[2] then last = tonumber(top[2]) end
if last > tonumber(ARGV[1]) then
  redis.call('ZADD', KEYS[2], last, ARGV[2])
  return 0
end
local existed = redis.call('ZREM', KEYS[2], ARGV[2])
redis.call('DEL', KEYS[1])
if existed == 1 then return last end
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
            new[] { KeyFor(userId), IndexKey },
            new RedisValue[] { now.ToUnixTimeMilliseconds(), now.Add(ttl).ToUnixTimeMilliseconds(), connectionId, (long)(ttl.TotalMilliseconds * 3), userId });
        return (int)result == 1;
    }

    public async Task<bool> RemoveAsync(int userId, string connectionId, DateTimeOffset now)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            RemoveScript,
            new[] { KeyFor(userId), IndexKey },
            new RedisValue[] { now.ToUnixTimeMilliseconds(), connectionId, userId });
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

    public async Task<IReadOnlyList<(int UserId, DateTimeOffset LastBeat)>> ExpireLapsedAsync(
        DateTimeOffset now, TimeSpan grace, TimeSpan ttl, Func<int, bool> skip, int max)
    {
        var db = _redis.GetDatabase();
        var cutoff = now.Subtract(grace).ToUnixTimeMilliseconds();
        var candidates = await db.SortedSetRangeByScoreWithScoresAsync(IndexKey, double.NegativeInfinity, cutoff, take: max);

        var gone = new List<(int, DateTimeOffset)>();
        foreach (var entry in candidates)
        {
            if (!int.TryParse(entry.Element.ToString(), out var userId) || skip(userId))
            {
                continue;
            }

            var lastBeatMs = (long)await db.ScriptEvaluateAsync(
                ExpireScript,
                new[] { KeyFor(userId), IndexKey },
                new RedisValue[] { cutoff, userId, (long)entry.Score });
            if (lastBeatMs > 0)
            {
                // The mark was set to lapse one ttl after the last beat.
                gone.Add((userId, DateTimeOffset.FromUnixTimeMilliseconds(lastBeatMs).Subtract(ttl)));
            }
        }

        return gone;
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
            // May lower the index below another replica's later mark; harmless, the sweeper re-checks the real marks before acting.
            tasks.Add(batch.SortedSetAddAsync(IndexKey, userId, lapse));
        }
        batch.Execute();
        await Task.WhenAll(tasks);
    }
}
