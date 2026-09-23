using System.Text.Json;

namespace AiChatAssistant.Api.Common;

/// <summary>The consistent <c>{ "error": { "code", "message" } }</c> shape all .NET error responses use.</summary>
public class ErrorResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ErrorResponse(string code, string message)
    {
        Error = new ErrorDetail(code, message);
    }

    public ErrorDetail Error { get; }

    public record ErrorDetail(string Code, string Message);

    /// <summary>
    /// Writes this error as the response body, for the paths that never reach a controller action
    /// (and so can't just `return BadRequest(new ErrorResponse(...))`) - unhandled exceptions,
    /// [Authorize] rejections, unmatched routes.
    /// </summary>
    public async Task WriteAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(this, JsonOptions));
    }
}
