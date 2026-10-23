using StackExchange.Redis;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Remembers which events a subscriber has already handled, because delivery is at-least-once: after a crash an event
/// can arrive twice, and with several API replicas the two copies can land on different replicas, so the memory must be
/// shared (Redis) to catch that.
/// </summary>
public interface IEventDeduplicator
{
    /// <returns>true the first time this id is seen (go ahead and handle it), false for a repeat.</returns>
    Task<bool> TryMarkSeenAsync(string eventId);
}

/// <summary>Per-process memory, bounded. Right for one replica, or when Redis is not configured.</summary>
public sealed class InMemoryEventDeduplicator : IEventDeduplicator
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _order = new();

    public InMemoryEventDeduplicator(int capacity = 10_000)
    {
        _capacity = capacity;
    }

    public Task<bool> TryMarkSeenAsync(string eventId)
    {
        lock (_gate)
        {
            if (!_seen.Add(eventId))
            {
                return Task.FromResult(false);
            }

            _order.Enqueue(eventId);
            if (_order.Count > _capacity)
            {
                _seen.Remove(_order.Dequeue()); // forget the oldest, so memory stays bounded
            }
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Shared memory in Redis: <c>SET key NX EX</c> succeeds for exactly one caller across all replicas. If Redis is
/// unreachable it lets the event through (a rare repeat is better than losing an event or stalling the queue).
/// </summary>
public sealed class RedisEventDeduplicator : IEventDeduplicator
{
    private static readonly TimeSpan Remember = TimeSpan.FromHours(24);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisEventDeduplicator> _logger;

    public RedisEventDeduplicator(IConnectionMultiplexer redis, ILogger<RedisEventDeduplicator> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public static string KeyFor(string eventId) => $"dedupe:direct-event:{eventId}";

    public async Task<bool> TryMarkSeenAsync(string eventId)
    {
        try
        {
            return await _redis.GetDatabase().StringSetAsync(KeyFor(eventId), "1", Remember, When.NotExists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Event de-duplication is unavailable; letting event {EventId} through", eventId);
            return true;
        }
    }
}
