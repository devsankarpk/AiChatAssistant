using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiChatAssistant.Api.Tests.TestSupport;

/// <summary>
/// Builds a ClaimsPrincipal shaped the way one actually looks after JWT validation in production
/// (see TokenService/Program.cs) - `sub` remapped to ClaimTypes.NameIdentifier, `role` remapped to
/// ClaimTypes.Role - so controller code reading `User.GetUserId()`/`User.IsAdmin()` behaves
/// identically to a real authenticated request, without going through actual token validation.
/// </summary>
public static class ClaimsPrincipalFactory
{
    // Single signature, not an overload with `params` on both - `Create(1, "Admin")` would
    // otherwise be genuinely ambiguous between "role Admin" and "email Admin" reads.
    public static ClaimsPrincipal Create(int userId, string? email = null, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    /// <summary>Attaches the principal to a controller so `User.GetUserId()` etc. work inside the action.</summary>
    public static void SetUser(this ControllerBase controller, ClaimsPrincipal user)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user },
        };
    }
}
