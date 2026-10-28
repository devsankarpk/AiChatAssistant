using System.IdentityModel.Tokens.Jwt;
using System.Text;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace AiChatAssistant.Api.Tests;

public class TokenServiceTests
{
    private const string Key = "unit-test-signing-key-unit-test-signing-key";

    private static TokenService Create(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["Jwt:Key"] = Key };
        foreach (var kv in extra ?? new()) settings[kv.Key] = kv.Value;
        return new TokenService(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static readonly User Someone = new() { Id = 7, Email = "me@example.com", Name = "Me" };

    private static JwtSecurityToken Read(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void Token_CarriesSubEmailAndEveryRole_UnderTheContractNames()
    {
        var (token, _) = Create().CreateToken(Someone, new[] { "User", "Admin" });
        var jwt = Read(token);

        Assert.Equal("7", jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal("me@example.com", jwt.Claims.Single(c => c.Type == "email").Value);
        Assert.Equal(new[] { "Admin", "User" }, jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).OrderBy(v => v));
    }

    [Fact]
    public void Token_HasAUniqueIdEachTime()
    {
        var service = Create();
        var a = Read(service.CreateToken(Someone, new[] { "User" }).Token).Id;
        var b = Read(service.CreateToken(Someone, new[] { "User" }).Token).Id;

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Expiry_DefaultsToSixtyMinutes_AndMatchesTheReturnedValue()
    {
        var (token, expiresAt) = Create().CreateToken(Someone, new[] { "User" });

        Assert.InRange(expiresAt, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        Assert.InRange((Read(token).ValidTo - expiresAt).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Expiry_ComesFromConfiguration()
    {
        var (_, expiresAt) = Create(new() { ["Jwt:ExpiryMinutes"] = "5" }).CreateToken(Someone, new[] { "User" });

        Assert.InRange(expiresAt, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));
    }

    [Fact]
    public void IssuerAndAudience_UseConfiguredValues_OrTheDocumentedDefaults()
    {
        var defaults = Read(Create().CreateToken(Someone, new[] { "User" }).Token);
        var custom = Read(Create(new() { ["Jwt:Issuer"] = "iss", ["Jwt:Audience"] = "aud" }).CreateToken(Someone, new[] { "User" }).Token);

        Assert.Equal("AiChatAssistant", defaults.Issuer);
        Assert.Contains("AiChatAssistant.Client", defaults.Audiences);
        Assert.Equal("iss", custom.Issuer);
        Assert.Contains("aud", custom.Audiences);
    }

    private static TokenValidationParameters Parameters(string key) => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        ClockSkew = TimeSpan.Zero,
    };

    [Fact]
    public void Signature_ValidatesWithTheSameKey_AndFailsWithAnother()
    {
        var token = Create().CreateToken(Someone, new[] { "User" }).Token;
        var handler = new JwtSecurityTokenHandler();

        handler.ValidateToken(token, Parameters(Key), out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(token, Parameters("another-signing-key-another-signing-key-1"), out _));
    }

    [Fact]
    public void ATamperedPayload_FailsValidation()
    {
        var token = Create().CreateToken(Someone, new[] { "User" }).Token;
        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("\"User\"", "\"Admin\"");
        var forged = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(forged, Parameters(Key), out _));
    }

    [Fact]
    public void MissingKey_FailsFast_WithAClearMessage()
    {
        var service = new TokenService(new ConfigurationBuilder().Build());

        var ex = Assert.Throws<InvalidOperationException>(() => service.CreateToken(Someone, new[] { "User" }));

        Assert.Contains("Jwt:Key", ex.Message);
        Assert.Contains("Jwt__Key", ex.Message);
    }
}
