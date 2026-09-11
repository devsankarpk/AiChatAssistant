namespace AiChatAssistant.Api.Common;

/// <summary>The consistent <c>{ "error": { "code", "message" } }</c> shape all .NET error responses use.</summary>
public class ErrorResponse
{
    public ErrorResponse(string code, string message)
    {
        Error = new ErrorDetail(code, message);
    }

    public ErrorDetail Error { get; }

    public record ErrorDetail(string Code, string Message);
}
