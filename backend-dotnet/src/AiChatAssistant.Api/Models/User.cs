namespace AiChatAssistant.Api.Models;

public class User
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the user's last live connection closed (written from Phase 11; null until then).</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// When the password last changed (change-password or a reset). Every token issued before this moment is refused, which is how
    /// a stolen or forgotten session stops working. Null means it never changed.
    /// </summary>
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>
    /// When the person confirmed their email address. People who register while verification is switched off, and everyone who existed
    /// before it was added, count as verified, so turning the requirement on later locks nobody out. Null means "still has to confirm".
    /// </summary>
    public DateTime? EmailVerifiedAt { get; set; }

    /// <summary>
    /// When an administrator disabled the account (null = active). A disabled account cannot log in, renew a session or use an existing token.
    /// Nothing is deleted: its chats and usage history stay, and enabling it again restores access.
    /// </summary>
    public DateTime? DisabledAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();

    public ICollection<UsageLog> UsageLogs { get; set; } = new List<UsageLog>();

    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();

    public ICollection<EmailVerificationToken> EmailVerificationTokens { get; set; } = new List<EmailVerificationToken>();

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
