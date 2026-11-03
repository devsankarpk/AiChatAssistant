namespace AiChatAssistant.Api.Services;

/// <summary>Settings for refresh tokens ("Sessions" section).</summary>
public class RefreshTokenOptions
{
    public const string SectionName = "Sessions";

    /// <summary>How long a refresh token lives. Every use issues a new one, so an active person stays signed in; an idle one is signed out after this long.</summary>
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>
    /// A token that was just rotated may legitimately arrive again (two tabs refreshing at once). Within this many seconds that is refused
    /// without revoking the family; later reuse is treated as theft.
    /// </summary>
    public int ReuseGraceSeconds { get; set; } = 10;
}
