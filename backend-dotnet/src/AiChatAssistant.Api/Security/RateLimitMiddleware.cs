using AiChatAssistant.Api.Common;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Security;

/// <summary>
/// Answers <c>429</c> in the standard error shape (with <c>Retry-After</c>) when a client sends too many requests to an endpoint marked
/// with <see cref="RateLimitAttribute"/>. Runs after authentication so the chat limit can count per user, and after forwarded-headers
/// handling so the auth limit counts the real client address instead of the proxy's.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimitCounter _counter;
    private readonly RateLimitOptions _options;
    private readonly ILogger<RateLimitMiddleware> _logger;

    public RateLimitMiddleware(RequestDelegate next, IRateLimitCounter counter, IOptions<RateLimitOptions> options, ILogger<RateLimitMiddleware> logger)
    {
        _next = next;
        _counter = counter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var marker = context.GetEndpoint()?.Metadata.GetMetadata<RateLimitAttribute>();
        if (!_options.Enabled || marker is null)
        {
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userId = context.User.GetUserId();
        var (limit, key) = marker.Policy switch
        {
            RateLimitPolicy.Chat => (_options.ChatPerMinute, userId != 0 ? $"chat:user:{userId}" : $"chat:ip:{ip}"),
            _ => (_options.AuthPerMinute, $"auth:ip:{ip}"),
        };

        var hit = await _counter.HitAsync(key, TimeSpan.FromSeconds(_options.WindowSeconds));
        context.Response.Headers["RateLimit-Limit"] = limit.ToString();
        context.Response.Headers["RateLimit-Remaining"] = Math.Max(0, limit - hit.Count).ToString();

        if (hit.Count <= limit)
        {
            await _next(context);
            return;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(hit.Remaining.TotalSeconds));
        context.Response.Headers["Retry-After"] = retryAfter.ToString();
        _logger.LogWarning("Rate limit exceeded: policy {Policy}, {Method} {Path}, retry after {RetryAfter}s", marker.Policy, context.Request.Method, context.Request.Path, retryAfter);
        await new ErrorResponse("rate_limited", $"Too many requests. Try again in {retryAfter} second{(retryAfter == 1 ? "" : "s")}.")
            .WriteAsync(context, StatusCodes.Status429TooManyRequests);
    }
}
