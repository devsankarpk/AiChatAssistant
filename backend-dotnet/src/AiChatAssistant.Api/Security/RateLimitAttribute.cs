namespace AiChatAssistant.Api.Security;

public enum RateLimitPolicy
{
    /// <summary>Counted per client IP. For the sign-up and password endpoints.</summary>
    Auth,

    /// <summary>Counted per signed-in user (per IP when there is no user). For anything that costs an LLM call.</summary>
    Chat,
}

/// <summary>Marks an action as rate limited. <see cref="RateLimitMiddleware"/> reads it from the endpoint, so a new endpoint is protected by adding this one line.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RateLimitAttribute : Attribute
{
    public RateLimitAttribute(RateLimitPolicy policy) => Policy = policy;

    public RateLimitPolicy Policy { get; }
}
