using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Publishes outbox rows to <c>direct.events</c>. A row is claimed first (a concurrency token makes two replicas
/// racing for it impossible to both win), then published with the broker's confirmation, then marked dispatched. If
/// the process dies between publish and mark, the claim lapses after the lease and the row is published again:
/// delivery is at-least-once, and every event carries its outbox id as the message id so a subscriber can ignore
/// repeats. Events are taken in id order and the dispatcher stops at the first failure, so a broker outage does not
/// reorder them. Old dispatched rows are deleted after the retention period.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IOutboxPublisher _publisher;
    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxDispatcher> _logger;
    private DateTime _lastCleanup = DateTime.MinValue;

    public OutboxDispatcher(
        IOutboxPublisher publisher,
        IServiceScopeFactory scopes,
        IOptions<OutboxOptions> options,
        TimeProvider time,
        ILogger<OutboxDispatcher> logger)
    {
        _publisher = publisher;
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(50, _options.PollMilliseconds));
        var delay = poll;
        _logger.LogInformation("Outbox dispatcher started (poll {Poll} ms, batch {Batch})", poll.TotalMilliseconds, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
                await CleanUpAsync(stoppingToken);
                delay = poll;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Broker or database trouble: wait longer each time (up to 30 s) and try again. Nothing is lost,
                // the rows are still in the database.
                delay = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, delay.TotalMilliseconds * 2));
                _logger.LogWarning(ex, "Outbox dispatch failed; trying again in {Seconds:0.#}s", delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Public so tests can drive one round without the timer.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;
        var leaseStart = now - TimeSpan.FromSeconds(_options.LeaseSeconds);

        var candidates = await db.OutboxMessages
            .Where(o => o.DispatchedAt == null && (o.ClaimedAt == null || o.ClaimedAt < leaseStart))
            .OrderBy(o => o.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var published = 0;
        foreach (var row in candidates)
        {
            row.ClaimedAt = now;
            row.ClaimToken = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(row).State = EntityState.Detached; // another replica claimed it first: theirs
                continue;
            }

            try
            {
                await _publisher.PublishAsync(row, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                row.Attempts++;
                row.ClaimedAt = null; // release at once so the next round retries it
                await db.SaveChangesAsync(CancellationToken.None);
                throw; // stop here: later events must not overtake this one
            }

            row.DispatchedAt = _time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            published++;
        }

        return published;
    }

    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - _lastCleanup < TimeSpan.FromMinutes(1))
        {
            return;
        }
        _lastCleanup = now;

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = now - TimeSpan.FromHours(_options.RetentionHours);
        var old = await db.OutboxMessages
            .Where(o => o.DispatchedAt != null && o.DispatchedAt < cutoff)
            .OrderBy(o => o.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        if (old.Count > 0)
        {
            db.OutboxMessages.RemoveRange(old);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Outbox: deleted {Count} dispatched event(s) older than {Hours} h", old.Count, _options.RetentionHours);
        }
    }
}
