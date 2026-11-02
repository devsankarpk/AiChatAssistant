namespace AiChatAssistant.Api.Security;

/// <summary>Config section "RateLimiting": fixed one-minute windows, counted in Redis when it is configured (so several replicas share one count) and in memory otherwise.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Per client IP, shared by register, login, forgot-password and reset-password: the endpoints a bot would hammer.</summary>
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Per signed-in user, for sending a chat message to the AI (each one costs LLM credit).</summary>
    public int ChatPerMinute { get; set; } = 20;

    public int WindowSeconds { get; set; } = 60;

    public void Validate()
    {
        if (AuthPerMinute < 1 || ChatPerMinute < 1 || WindowSeconds < 1)
        {
            throw new InvalidOperationException("RateLimiting:AuthPerMinute, ChatPerMinute and WindowSeconds must each be at least 1 (set RateLimiting:Enabled to false to switch limiting off).");
        }
    }
}

/// <summary>Config section "Lockout": too many failed logins for one email lock that email for the rest of the window.</summary>
public sealed class LockoutOptions
{
    public const string SectionName = "Lockout";

    public bool Enabled { get; set; } = true;

    /// <summary>The failed attempt that reaches this number locks the email.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>The window starts at the first failure; a locked email unlocks when it ends. A successful login or a password reset clears the count.</summary>
    public int LockoutMinutes { get; set; } = 15;

    public void Validate()
    {
        if (MaxFailedAttempts < 1 || LockoutMinutes < 1)
        {
            throw new InvalidOperationException("Lockout:MaxFailedAttempts and Lockout:LockoutMinutes must each be at least 1 (set Lockout:Enabled to false to switch lockout off).");
        }
    }
}

/// <summary>
/// Config section "ForwardedHeaders": which proxies the API believes about the client's address. Only requests that arrive from one of the
/// known networks may set X-Forwarded-For / X-Forwarded-Proto; from anywhere else those headers are ignored, so a client cannot pick its own IP.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public bool Enabled { get; set; } = true;

    /// <summary>CIDR ranges, e.g. "10.0.0.0/8". The default covers loopback and the private ranges Docker and Kubernetes networks use.</summary>
    public string[] KnownNetworks { get; set; } = Array.Empty<string>();

    /// <summary>Individual proxy addresses.</summary>
    public string[] KnownProxies { get; set; } = Array.Empty<string>();
}
