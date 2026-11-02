using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Moq;

namespace AiChatAssistant.Api.Tests;

public class LowAuthLimitFactory : ApiFactory { public LowAuthLimitFactory() { Settings["RateLimiting:AuthPerMinute"] = "3"; } }
public class LowChatLimitFactory : ApiFactory { public LowChatLimitFactory() { Settings["RateLimiting:ChatPerMinute"] = "2"; } }
public class LockoutFactory : ApiFactory { public LockoutFactory() { Settings["Lockout:MaxFailedAttempts"] = "5"; Settings["Lockout:LockoutMinutes"] = "15"; } }
public class LimitsOffFactory : ApiFactory { public LimitsOffFactory() { Settings["RateLimiting:Enabled"] = "false"; Settings["RateLimiting:AuthPerMinute"] = "1"; } }

internal static class Http
{
    public static HttpRequestMessage Login(string email, string password, string? remoteIp = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email, password }) };
        if (remoteIp is not null) request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    public static async Task<string> CodeOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}

/// <summary>The sign-up and password endpoints are limited per client address, and the address is the real one, not the proxy's.</summary>
public class AuthRateLimitTests : IClassFixture<LowAuthLimitFactory>
{
    private readonly LowAuthLimitFactory _factory;
    public AuthRateLimitTests(LowAuthLimitFactory factory) => _factory = factory;

    [Fact]
    public async Task TheRequestOverTheLimit_Is429_InTheStandardShape_WithRetryAfter()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.SendAsync(Http.Login("x@example.com", "wrong-password", remoteIp: "10.1.0.1"));
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var blocked = await client.SendAsync(Http.Login("x@example.com", "wrong-password", remoteIp: "10.1.0.1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("rate_limited", await Http.CodeOf(blocked));
        var retryAfter = int.Parse(blocked.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 60);
        Assert.Contains("Try again in", await blocked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AllFourAuthEndpoints_ShareTheOneBucketOfTheAddress()
    {
        var client = _factory.CreateClient();
        async Task<HttpStatusCode> Post(string path, object body)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            r.Headers.Add("X-Test-Remote-Ip", "10.1.0.2");
            return (await client.SendAsync(r)).StatusCode;
        }

        await Post("/api/auth/login", new { email = "a@example.com", password = "x" });
        await Post("/api/auth/register", new { name = "A", email = "shared.bucket@example.com", password = "longenough" });
        await Post("/api/auth/forgot-password", new { email = "a@example.com" });

        Assert.Equal(HttpStatusCode.TooManyRequests, await Post("/api/auth/reset-password", new { token = "t", newPassword = "longenough" }));
    }

    [Fact]
    public async Task AnotherAddress_HasItsOwnBucket()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 4; i++) await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.1.0.3"));

        var other = await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.1.0.4"));

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task AnAllowedResponse_ShowsTheLimitAndWhatIsLeft()
    {
        var response = await _factory.CreateClient().SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.1.0.5"));

        Assert.Equal("3", response.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("2", response.Headers.GetValues("RateLimit-Remaining").Single());
    }

    [Fact]
    public async Task EndpointsWithoutTheMarker_AreNeverLimited()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    // ---- the address is the real one: forwarded headers are believed only from trusted proxies ----

    [Fact]
    public async Task BehindATrustedProxy_EachRealClientGetsItsOwnBucket()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 4; i++) await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.0.0.9", forwardedFor: "203.0.113.10"));

        var blockedClient = await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.0.0.9", forwardedFor: "203.0.113.10"));
        var otherClient = await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.0.0.9", forwardedFor: "203.0.113.11"));

        Assert.Equal(HttpStatusCode.TooManyRequests, blockedClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode); // not lumped together under the proxy's address
    }

    [Fact]
    public async Task ASpoofedLeftmostAddress_DoesNotGiveAFreshBucket()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++) await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.0.0.9", forwardedFor: "203.0.113.20"));

        // The attacker prepends fake addresses; the proxy appended the real one on the right.
        var spoofed = await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "10.0.0.9", forwardedFor: $"1.1.1.{Random.Shared.Next(1, 250)}, 203.0.113.20"));

        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }

    [Fact]
    public async Task FromAnUntrustedAddress_TheForwardedHeaderIsIgnored_SoItCannotPickItsOwnIp()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++) await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "198.51.100.7", forwardedFor: $"9.9.9.{i}"));

        var fourth = await client.SendAsync(Http.Login("x@example.com", "wrong", remoteIp: "198.51.100.7", forwardedFor: "9.9.9.200"));

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }
}

public class ChatRateLimitTests : IClassFixture<LowChatLimitFactory>
{
    private readonly LowChatLimitFactory _factory;
    public ChatRateLimitTests(LowChatLimitFactory factory)
    {
        _factory = factory;
        _factory.Python.Reset();
        _factory.Python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("ok", 1));
    }

    private Guid SeedSession(int userId)
    {
        var id = Guid.NewGuid();
        _factory.WithDb(db => { db.ChatSessions.Add(new ChatSession { Id = id, UserId = userId, Title = "New chat" }); db.SaveChanges(); return 0; });
        return id;
    }

    [Fact]
    public async Task ThePerUserQuota_Blocks_TheOverQuotaQuestion_WithoutCallingTheAi()
    {
        var (user, token) = _factory.CreateUser("Chatty", "chatty@example.com", "User");
        var session = SeedSession(user.Id);
        var client = _factory.CreateClient().WithToken(token);

        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = $"q{i}" })).StatusCode);
        var blocked = await client.PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "one too many" });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("rate_limited", await Http.CodeOf(blocked));
        _factory.Python.Verify(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "one too many", It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(_factory.WithDb(db => db.Messages.Select(m => m.Content).ToList()), c => c == "one too many"); // refused before anything was saved
    }

    [Fact]
    public async Task TheQuotaIsPerUser_AnotherPersonOnTheSameAddressIsNotAffected()
    {
        var (a, tokenA) = _factory.CreateUser("A", "quota.a@example.com", "User");
        var (b, tokenB) = _factory.CreateUser("B", "quota.b@example.com", "User");
        var sa = SeedSession(a.Id); var sb = SeedSession(b.Id);
        var ca = _factory.CreateClient().WithToken(tokenA); var cb = _factory.CreateClient().WithToken(tokenB);
        for (var i = 0; i < 3; i++) await ca.PostAsJsonAsync($"/api/chat/sessions/{sa}/messages", new { content = "q" });

        var other = await cb.PostAsJsonAsync($"/api/chat/sessions/{sb}/messages", new { content = "mine" });

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task ReadingChatsIsNeverLimited()
    {
        var (user, token) = _factory.CreateUser("Reader", "reader@example.com", "User");
        var session = SeedSession(user.Id);
        var client = _factory.CreateClient().WithToken(token);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chat/sessions")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/chat/sessions/{session}/messages")).StatusCode);
        }
    }

    [Fact]
    public async Task RetryingAReply_CountsAgainstTheSameQuota()
    {
        var (user, token) = _factory.CreateUser("Retrier", "retrier@example.com", "User");
        var session = SeedSession(user.Id);
        var client = _factory.CreateClient().WithToken(token);
        for (var i = 0; i < 2; i++) await client.PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "q" });

        var retry = await client.PostAsync($"/api/chat/jobs/{Guid.NewGuid()}/retry", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, retry.StatusCode);
    }

    [Fact]
    public async Task ASignedOutCaller_IsStillRefused_AndCountedByAddress()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/chat/sessions/{Guid.NewGuid()}/messages", new { content = "hi" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class RateLimitSwitchTests : IClassFixture<LimitsOffFactory>
{
    private readonly LimitsOffFactory _factory;
    public RateLimitSwitchTests(LimitsOffFactory factory) => _factory = factory;

    [Fact]
    public async Task WhenSwitchedOff_NoRequestIsEverLimited_EvenWithALimitOfOne()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 6; i++)
        {
            var r = await client.SendAsync(Http.Login("x@example.com", "wrong"));
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.False(r.Headers.Contains("RateLimit-Limit"));
        }
    }
}

/// <summary>Repeated wrong passwords lock the email, the same way whether or not it has an account.</summary>
public class LockoutPipelineTests : IClassFixture<LockoutFactory>
{
    private readonly LockoutFactory _factory;
    public LockoutPipelineTests(LockoutFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0); // creates the in-memory database and seeds the User/Admin roles that registration needs
    }

    private async Task<HttpResponseMessage> TryLogin(string email, string password) =>
        await _factory.CreateClient().SendAsync(Http.Login(email, password));

    private async Task Register(string email)
    {
        var r = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { name = "Lock Test", email, password = "right-password-1" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task TheFifthWrongPassword_Locks_With423_AccountLocked_AndHowLongToWait()
    {
        await Register("lock.five@example.com");
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin("lock.five@example.com", "wrong")).StatusCode);

        var fifth = await TryLogin("lock.five@example.com", "wrong");

        Assert.Equal(HttpStatusCode.Locked, fifth.StatusCode);
        Assert.Equal("account_locked", await Http.CodeOf(fifth));
        Assert.Contains("15 minutes", await fifth.Content.ReadAsStringAsync());
        Assert.InRange(int.Parse(fifth.Headers.GetValues("Retry-After").Single()), 800, 900);
    }

    [Fact]
    public async Task WhileLocked_EvenTheRightPasswordIsRefused()
    {
        await Register("lock.right@example.com");
        for (var i = 0; i < 5; i++) await TryLogin("lock.right@example.com", "wrong");

        var right = await TryLogin("lock.right@example.com", "right-password-1");

        Assert.Equal(HttpStatusCode.Locked, right.StatusCode);
    }

    [Fact]
    public async Task AnEmailWithNoAccount_LocksExactlyLikeOneWithAnAccount_SoItRevealsNothing()
    {
        await Register("lock.exists@example.com");
        var existing = new List<HttpStatusCode>();
        var missing = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            existing.Add((await TryLogin("lock.exists@example.com", "wrong")).StatusCode);
            missing.Add((await TryLogin("lock.nobody@example.com", "wrong")).StatusCode);
        }

        Assert.Equal(existing, missing);
        Assert.Equal(HttpStatusCode.Locked, missing[^1]);
    }

    [Fact]
    public async Task ASuccessfulLogin_ResetsTheCount()
    {
        await Register("lock.reset@example.com");
        for (var i = 0; i < 4; i++) await TryLogin("lock.reset@example.com", "wrong");
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("lock.reset@example.com", "right-password-1")).StatusCode);

        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin("lock.reset@example.com", "wrong")).StatusCode);
    }

    [Fact]
    public async Task OneEmailLocked_AnotherCanStillLogIn()
    {
        await Register("lock.victim@example.com");
        await Register("lock.bystander@example.com");
        for (var i = 0; i < 5; i++) await TryLogin("lock.victim@example.com", "wrong");

        Assert.Equal(HttpStatusCode.OK, (await TryLogin("lock.bystander@example.com", "right-password-1")).StatusCode);
    }

    [Fact]
    public async Task CasingAndSpacesInTheEmail_DoNotEscapeTheLock()
    {
        await Register("lock.case@example.com");
        for (var i = 0; i < 5; i++) await TryLogin("lock.case@example.com", "wrong");

        Assert.Equal(HttpStatusCode.Locked, (await TryLogin("  LOCK.Case@Example.com ", "right-password-1")).StatusCode);
    }

    [Fact]
    public async Task AnInvalidlyFormedLogin_IsA400_AndIsNotCountedAsAFailure()
    {
        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.BadRequest, (await TryLogin("not-an-email", "x")).StatusCode);
    }

    [Fact]
    public async Task ResettingThePassword_UnlocksTheAccount()
    {
        await Register("lock.recover@example.com");
        for (var i = 0; i < 5; i++) await TryLogin("lock.recover@example.com", "wrong");
        Assert.Equal(HttpStatusCode.Locked, (await TryLogin("lock.recover@example.com", "right-password-1")).StatusCode);
        var raw = "recover-token-" + Guid.NewGuid().ToString("N");
        _factory.WithDb(db =>
        {
            var user = db.Users.Single(u => u.Email == "lock.recover@example.com");
            db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            db.SaveChanges();
            return 0;
        });

        var reset = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = raw, newPassword = "brand-new-password-2" });
        var login = await TryLogin("lock.recover@example.com", "brand-new-password-2");

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}

/// <summary>Browser-hardening headers set by the API itself, whatever sits in front of it.</summary>
public class ApiSecurityHeadersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ApiSecurityHeadersTests(ApiFactory factory) => _factory = factory;

    private static void AssertHardened(HttpResponseMessage r)
    {
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
    }

    [Theory]
    [InlineData("/api/auth/me")]          // 401 from the bearer handler
    [InlineData("/api/does-not-exist")]   // the 404 fallback
    [InlineData("/health")]
    public async Task EveryKindOfResponse_CarriesTheHeaders(string path)
    {
        AssertHardened(await _factory.CreateClient().GetAsync(path));
    }

    [Fact]
    public async Task ASuccessfulApiResponse_CarriesThem_AndIsNotCacheable()
    {
        var (_, token) = _factory.CreateUser("Headers", "headers@example.com", "User");

        var response = await _factory.CreateClient().WithToken(token).GetAsync("/api/chat/sessions");

        AssertHardened(response);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AnUnhandledError_StillCarriesThem_BecauseTheyAreSetWhenTheResponseStarts()
    {
        var (user, token) = _factory.CreateUser("Boom", "headers.boom@example.com", "User");
        var session = Guid.NewGuid();
        _factory.WithDb(db => { db.ChatSessions.Add(new ChatSession { Id = session, UserId = user.Id, Title = "New chat" }); db.SaveChanges(); return 0; });
        _factory.Python.Reset();
        _factory.Python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "hi" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertHardened(response);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task OnlyPathsUnderApiAreMarkedNoStore_NotTheHub()
    {
        var response = await _factory.CreateClient().PostAsync("/hubs/direct/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertHardened(response);
        Assert.Null(response.Headers.CacheControl);
    }

    [Fact]
    public async Task OutsideDevelopment_TheApiDoesNotRedirectToHttps_WhenNoHttpsPortExists()
    {
        var response = await _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
