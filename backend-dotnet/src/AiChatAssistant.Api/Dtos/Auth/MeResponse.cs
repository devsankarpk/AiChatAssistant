namespace AiChatAssistant.Api.Dtos.Auth;

/// <summary>What <c>GET /api/auth/me</c> returns — read straight off the caller's JWT claims, no DB round trip.</summary>
public class MeResponse
{
    public int Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public IReadOnlyCollection<string> Roles { get; set; } = Array.Empty<string>();
}
