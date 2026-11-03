namespace AiChatAssistant.Api.Dtos.Auth;

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Single-use; exchange it at POST /api/auth/refresh for a new pair.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    public DateTime RefreshExpiresAt { get; set; }

    public UserSummary User { get; set; } = null!;
}

public class UserSummary
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public IReadOnlyCollection<string> Roles { get; set; } = Array.Empty<string>();
}
