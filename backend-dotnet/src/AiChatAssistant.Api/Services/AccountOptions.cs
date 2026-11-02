namespace AiChatAssistant.Api.Services;

/// <summary>Config section "Account".</summary>
public sealed class AccountOptions
{
    public const string SectionName = "Account";

    /// <summary>
    /// When true, registering sends a "confirm your email" link and the person cannot log in until they click it; registration then answers
    /// the same generic 202 whether or not the address already has an account. When false (the default), registering signs the person in at once
    /// and an address that is taken is a 409 (the sign-up endpoint is rate limited, which slows anyone probing for registered emails).
    /// </summary>
    public bool RequireEmailVerification { get; set; }

    /// <summary>How long a confirmation link stays valid.</summary>
    public int VerificationLinkHours { get; set; } = 24;
}
