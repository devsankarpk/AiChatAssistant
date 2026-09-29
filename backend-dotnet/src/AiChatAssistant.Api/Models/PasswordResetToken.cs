namespace AiChatAssistant.Api.Models;

/// <summary>
/// Only the SHA-256 hash of the raw token is ever stored - the raw value exists only in the
/// email link, so a leaked database alone can't be used to reset anyone's password.
/// </summary>
public class PasswordResetToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
