namespace AiChatAssistant.Api.Dtos.Auth;

/// <summary>Generic success body for endpoints that only need to confirm "it worked" (forgot/reset
/// password) - no token, no user summary.</summary>
public class AuthMessageResponse
{
    public AuthMessageResponse(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
