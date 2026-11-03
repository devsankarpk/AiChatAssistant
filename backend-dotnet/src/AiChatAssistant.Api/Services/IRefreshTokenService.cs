using AiChatAssistant.Api.Models;

namespace AiChatAssistant.Api.Services;

public enum RefreshOutcome { Rotated, Invalid, ReuseDetected }

public record RefreshResult(RefreshOutcome Outcome, User? User = null, string? NewToken = null, DateTime? NewExpiresAt = null);

public interface IRefreshTokenService
{
    /// <summary>Starts a new family (a login) and returns the raw token, which exists nowhere else.</summary>
    Task<(string Token, DateTime ExpiresAt)> IssueAsync(int userId, CancellationToken cancellationToken);

    /// <summary>Exchanges a token for the next one in its family. Unknown, expired, revoked and lost-race tokens are <see cref="RefreshOutcome.Invalid"/>.</summary>
    Task<RefreshResult> RotateAsync(string rawToken, CancellationToken cancellationToken);

    /// <summary>Revokes the family of this token (idempotent; an unknown token is ignored).</summary>
    Task RevokeAsync(string rawToken, CancellationToken cancellationToken);

    /// <summary>Revokes every refresh token of the user (password change or reset, "sign out everywhere").</summary>
    Task RevokeAllAsync(int userId, CancellationToken cancellationToken);
}
