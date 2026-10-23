using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Realtime;

/// <summary>Marks this process's connections as still alive, on a timer, so a replica that dies stops being counted.</summary>
public sealed class PresenceHeartbeatService : BackgroundService
{
    private readonly IPresenceTracker _presence;
    private readonly TimeSpan _interval;

    public PresenceHeartbeatService(IPresenceTracker presence, IOptions<PresenceOptions> options)
    {
        _presence = presence;
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.HeartbeatSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _presence.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
