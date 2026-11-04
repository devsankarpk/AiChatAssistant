using System.Security.Claims;
using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

/// <summary>
/// The checks a signed token cannot make about itself, run on every authenticated request: the account must still exist (a deleted account's
/// token stops working at once) and the token must have been issued after the last password change (a stolen or forgotten session stops
/// working when the password changes). One primary-key lookup per request.
/// </summary>
public static class AccountTokenValidator
{
    /// <returns>null when the token is fine, otherwise the reason it is refused (logged, never shown to the caller).</returns>
    public static async Task<string?> ValidateAsync(AppDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        if (userId == 0)
        {
            return "the token has no user";
        }

        var changedAt = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PasswordChangedAt, u.DisabledAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (changedAt is null)
        {
            return "the account no longer exists";
        }

        if (changedAt.DisabledAt is not null)
        {
            return "the account is disabled";
        }

        if (changedAt.PasswordChangedAt is { } changed)
        {
            // Whole seconds on both sides. A token with no (or an unreadable) issue time predates the stamp, so it is refused.
            var changedSeconds = new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc)).ToUnixTimeSeconds();
            if (!long.TryParse(principal.FindFirstValue("iat"), out var issuedSeconds) || issuedSeconds < changedSeconds)
            {
                return "the password was changed after this token was issued";
            }
        }

        return null;
    }
}
