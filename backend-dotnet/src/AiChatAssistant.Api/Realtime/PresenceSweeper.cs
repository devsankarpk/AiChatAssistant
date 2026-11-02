using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Realtime;

/// <summary>
/// Notices people whose API replica crashed. A replica that is killed runs no disconnect code, so nobody saves their last-seen time or
/// tells their contacts; their presence marks simply lapse. This service finds those people and does what a clean disconnect would have done.
/// Every replica runs one; the shared store hands each lapsed person to exactly one of them.
/// </summary>
public sealed class PresenceSweeper : BackgroundService
{
    private readonly IPresenceTracker _presence;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PresenceSweeper> _logger;
    private readonly TimeSpan _interval;

    public PresenceSweeper(IPresenceTracker presence, IServiceScopeFactory scopes, IOptions<PresenceOptions> options, ILogger<PresenceSweeper> logger)
    {
        _presence = presence;
        _scopes = scopes;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>One pass. Public so a test can run it without waiting for the timer. Never throws.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lapsed = await _presence.SweepLapsedAsync(cancellationToken);
            foreach (var person in lapsed)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PresenceAnnouncer>().AnnounceOfflineAsync(person.UserId, person.LastSeenUtc);
                    _logger.LogInformation("Presence swept: user {UserId} went offline without a clean disconnect (their server stopped), last seen {LastSeen:o}", person.UserId, person.LastSeenUtc);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not announce user {UserId} offline after a sweep", person.UserId);
                }
            }

            return lapsed.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Presence sweep failed; will try again");
            return 0;
        }
    }
}
