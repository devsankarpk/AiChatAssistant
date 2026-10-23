namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Settings for AI replies (config section "Chat"). With <see cref="AsyncReplies"/> off (the default) the
/// send-message request waits for the AI and answers 200 with both messages, as it always did. With it on, the
/// request answers 202 at once and a background worker produces the reply (Phase 18).
/// Array defaults live in appsettings.json for the same reason as Email:RetryDelaysSeconds.
/// </summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public bool AsyncReplies { get; set; }

    /// <summary>Wait before each retry after the AI call failed. Its length is the number of retries (two = three attempts).</summary>
    public int[] ReplyRetryDelaysSeconds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// How many AI calls one worker runs at once. Protects the AI provider's rate limit: with N API replicas
    /// the whole system runs at most N times this many.
    /// </summary>
    public int ReplyPrefetch { get; set; } = 4;

    /// <summary>
    /// How long a worker may hold a job before another may take it over (a crashed worker never releases it).
    /// It must be longer than the slowest AI call: .NET gives Python 60 s (see CLAUDE.md, nested timeouts).
    /// </summary>
    public int ReplyLeaseSeconds { get; set; } = 90;
}
