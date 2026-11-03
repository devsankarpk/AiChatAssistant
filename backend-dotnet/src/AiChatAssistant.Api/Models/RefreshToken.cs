namespace AiChatAssistant.Api.Models;

/// <summary>
/// A long-lived, single-use credential that exchanges for a new short-lived access token. Only the SHA-256 hash is stored. Every use
/// rotates it (the old row gets <see cref="UsedAt"/>, a new one in the same <see cref="FamilyId"/> is issued), so a copied token works
/// at most once; presenting a used token again revokes the whole family (theft detection).
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    /// <summary>One login = one family; rotations stay in it. Revoking a family signs that one device out.</summary>
    public string FamilyId { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when this token was exchanged for the next one. Also the concurrency token, so two simultaneous refreshes cannot both win.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>Set by logout, password change, or reuse detection.</summary>
    public DateTime? RevokedAt { get; set; }

    public User User { get; set; } = null!;
}
