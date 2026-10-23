namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Behaviour of the password-reset email queue (config section "Email"). The defaults live in
/// appsettings.json rather than in these initialisers: .NET's configuration binder appends to an
/// array that already has values, so a default of {10, 60} overridden with one environment value
/// would not replace the first entry cleanly.
/// </summary>
public sealed class EmailQueueOptions
{
    public const string SectionName = "Email";

    /// <summary>"smtp" (real mail) or "console" (logs the link; dev and tests only).</summary>
    public string Provider { get; set; } = "smtp";

    /// <summary>
    /// Wait before each retry after a failed send, in seconds. Its length is the number of retries
    /// (two entries = three attempts in all). Each distinct delay gets its own retry queue.
    /// </summary>
    public int[] RetryDelaysSeconds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// How long a queued message stays useful. Must not exceed the reset token's lifetime (1 hour):
    /// after that the link inside it no longer works, so sending it would only confuse the person.
    /// </summary>
    public int MessageTtlMinutes { get; set; } = 60;

    /// <summary>How many emails the worker handles at once; protects the mail server's rate limit.</summary>
    public int Prefetch { get; set; } = 4;
}
