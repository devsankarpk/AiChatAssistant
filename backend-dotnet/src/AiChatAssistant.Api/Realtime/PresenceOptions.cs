namespace AiChatAssistant.Api.Realtime;

/// <summary>
/// Timing of shared presence (config section "Presence"). Each API replica marks its connections as alive
/// every <see cref="HeartbeatSeconds"/>; a mark lapses after <see cref="TtlSeconds"/>, so if a replica dies
/// its users drop offline within that time. Keep the heartbeat well under a third of the TTL, so a slow
/// beat or a short pause never makes a live user look offline.
/// </summary>
public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    public int TtlSeconds { get; set; } = 45;
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>
    /// How often each replica looks for people whose every connection lapsed without a clean disconnect (their replica
    /// crashed). Several replicas sweeping at once is safe: the Redis script lets exactly one of them take each person.
    /// </summary>
    public int SweepSeconds { get; set; } = 10;

    /// <summary>
    /// How long past its lapse a mark must be before the sweeper believes its replica is gone. Without this, a Redis that was
    /// paused or unreachable for a while would have every mark lapsed when it returns, and the sweeper (running before the next
    /// heartbeat re-creates them) would announce everyone offline. Keep it longer than one heartbeat. Each replica also
    /// pauses its own sweeping for this long plus one heartbeat after it last failed to reach Redis, so a longer outage cannot cause it either.
    /// </summary>
    public int SweepGraceSeconds { get; set; } = 20;
}
