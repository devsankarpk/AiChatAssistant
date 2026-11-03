namespace AiChatAssistant.Api.Dtos.Auth;

public class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class LogoutRequest
{
    public string? RefreshToken { get; set; }
}
