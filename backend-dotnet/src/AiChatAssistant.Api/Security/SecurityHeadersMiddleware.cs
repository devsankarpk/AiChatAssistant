namespace AiChatAssistant.Api.Security;

/// <summary>
/// Browser-hardening headers on every API response, whoever sits in front of it: no content-type sniffing, no framing, no referrer, and
/// no caching of anything under /api (the answers are per person). Set when the response starts, so they also survive the
/// exception middleware clearing a failed response.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });
        return _next(context);
    }
}
