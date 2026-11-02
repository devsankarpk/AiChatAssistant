using System.Collections.Concurrent;
using StackExchange.Redis;

namespace AiChatAssistant.Api.Security;

/// <summary>How many times a key was hit in its current window and how long until the window ends.</summary>
public readonly record struct RateLimitCount(long Count, TimeSpan Remaining);

/// <summary>
/// A fixed-window hit counter, the one primitive both the request limiter and the login lockout are built on.
/// <see cref="HitAsync"/> counts one more hit (the first hit starts the window); <see cref="PeekAsync"/> reads without counting.
/// </summary>
public interface IRateLimitCounter
{
    Task<RateLimitCount> HitAsync(string key, TimeSpan window);
    Task<RateLimitCount> PeekAsync(string key);
    Task ResetAsync(string key);
}

/// <summary>Per-process counters. Right for one API replica; with several, each keeps its own count.</summary>
public sealed class InMemoryRateLimitCounter : IRateLimitCounter
{
    private readonly ConcurrentDictionary<string, (long Count, DateTimeOffset EndsAt)> _entries = new();
    private readonly TimeProvider _time;
    private long _lastSweepTicks;

    public InMemoryRateLimitCounter(TimeProvider time) => _time = time;

    public Task<RateLimitCount> HitAsync(string key, TimeSpan window)
    {
        var now = _time.GetUtcNow();
        Sweep(now);
        var entry = _entries.AddOrUpdate(
            key,
            _ => (1, now + window),
            (_, old) => old.EndsAt <= now ? (1, now + window) : (old.Count + 1, old.EndsAt));
        return Task.FromResult(new RateLimitCount(entry.Count, entry.EndsAt - now));
    }

    public Task<RateLimitCount> PeekAsync(string key)
    {
        var now = _time.GetUtcNow();
        return Task.FromResult(_entries.TryGetValue(key, out var e) && e.EndsAt > now
            ? new RateLimitCount(e.Count, e.EndsAt - now)
            : new RateLimitCount(0, TimeSpan.Zero));
    }

    public Task ResetAsync(string key)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    // Keeps the dictionary from growing forever with one-off clients: drop ended windows at most once a minute.
    private void Sweep(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < TimeSpan.TicksPerMinute || Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (pair.Value.EndsAt <= now)
            {
                _entries.TryRemove(pair);
            }
        }
    }
}

/// <summary>Counters in Redis, so every API replica sees the same count. One Lua script makes "count and start the window" atomic.</summary>
public sealed class RedisRateLimitCounter : IRateLimitCounter
{
    // KEYS[1] = counter, ARGV[1] = window ms. Returns { count, ms left }.
    private const string HitScript = @"
local count = redis.call('INCR', KEYS[1])
if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
local ttl = redis.call('PTTL', KEYS[1])
if ttl < 0 then redis.call('PEXPIRE', KEYS[1], ARGV[1]); ttl = tonumber(ARGV[1]) end
return { count, ttl }";

    private readonly IConnectionMultiplexer _redis;

    public RedisRateLimitCounter(IConnectionMultiplexer redis) => _redis = redis;

    public static RedisKey KeyFor(string key) => "ratelimit:" + key;

    public async Task<RateLimitCount> HitAsync(string key, TimeSpan window)
    {
        var result = (long[]?)await _redis.GetDatabase().ScriptEvaluateAsync(HitScript, new[] { KeyFor(key) }, new RedisValue[] { (long)window.TotalMilliseconds });
        return new RateLimitCount(result![0], TimeSpan.FromMilliseconds(result[1]));
    }

    public async Task<RateLimitCount> PeekAsync(string key)
    {
        var db = _redis.GetDatabase();
        var value = db.StringGetAsync(KeyFor(key));
        var ttl = db.KeyTimeToLiveAsync(KeyFor(key));
        await Task.WhenAll(value, ttl);
        return value.Result.IsNull ? new RateLimitCount(0, TimeSpan.Zero) : new RateLimitCount((long)value.Result, ttl.Result ?? TimeSpan.Zero);
    }

    public Task ResetAsync(string key) => _redis.GetDatabase().KeyDeleteAsync(KeyFor(key));
}

/// <summary>
/// Redis when it answers, this process's own memory when it does not. A Redis outage therefore weakens limiting to one count per replica;
/// it never switches limiting off and never turns into an error for the person making the request.
/// </summary>
public sealed class ResilientRateLimitCounter : IRateLimitCounter
{
    private readonly IRateLimitCounter _primary;
    private readonly IRateLimitCounter _fallback;
    private readonly ILogger<ResilientRateLimitCounter> _logger;

    public ResilientRateLimitCounter(IRateLimitCounter primary, IRateLimitCounter fallback, ILogger<ResilientRateLimitCounter> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public Task<RateLimitCount> HitAsync(string key, TimeSpan window) => Try(c => c.HitAsync(key, window));
    public Task<RateLimitCount> PeekAsync(string key) => Try(c => c.PeekAsync(key));

    public async Task ResetAsync(string key)
    {
        // Clear both: a count may have been kept locally while Redis was away.
        await _fallback.ResetAsync(key);
        try
        {
            await _primary.ResetAsync(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Rate limit store unavailable while clearing a counter");
        }
    }

    private async Task<RateLimitCount> Try(Func<IRateLimitCounter, Task<RateLimitCount>> action)
    {
        try
        {
            return await action(_primary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Rate limit store unavailable; counting in this server's memory instead");
            return await action(_fallback);
        }
    }
}
