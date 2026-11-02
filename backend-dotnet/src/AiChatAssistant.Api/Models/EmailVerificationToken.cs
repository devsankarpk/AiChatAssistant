namespace AiChatAssistant.Api.Models;

/// <summary>
/// Same idea as <see cref="PasswordResetToken"/>: only the SHA-256 hash of the raw token is stored; the raw value exists only in the
/// emailed link. Single use, and asking for a new link invalidates the older unused ones.
/// </summary>
public class EmailVerificationToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
