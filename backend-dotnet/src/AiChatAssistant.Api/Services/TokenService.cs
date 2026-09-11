using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AiChatAssistant.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace AiChatAssistant.Api.Services;

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public (string Token, DateTime ExpiresAt) CreateToken(User user, IReadOnlyCollection<string> roles)
    {
        var key = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured. Set it via 'dotnet user-secrets set \"Jwt:Key\" \"<value>\"' in Development, or the Jwt__Key environment variable elsewhere.");
        var issuer = _configuration["Jwt:Issuer"] ?? "AiChatAssistant";
        var audience = _configuration["Jwt:Audience"] ?? "AiChatAssistant.Client";
        var expiryMinutes = _configuration.GetValue("Jwt:ExpiryMinutes", 60);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        // Plain "role" (not ClaimTypes.Role) so the encoded JWT payload literally carries sub/email/role,
        // per the cross-service contract in CLAUDE.md. ASP.NET Core's default inbound claim map still
        // rehydrates this into ClaimTypes.Role on validation, so [Authorize(Roles = "Admin")] works unchanged.
        claims.AddRange(roles.Select(role => new Claim("role", role)));

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: signingCredentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
