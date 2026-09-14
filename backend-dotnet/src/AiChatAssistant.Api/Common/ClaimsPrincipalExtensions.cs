using System.Security.Claims;

namespace AiChatAssistant.Api.Common;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The caller's `sub` claim (User.Id), as ASP.NET Core rehydrates it into ClaimTypes.NameIdentifier.</summary>
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.TryParse(subject, out var id) ? id : 0;
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Models.Role.Admin);
}
