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
}
