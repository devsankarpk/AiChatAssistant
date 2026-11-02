using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Tests.TestSupport;
using Moq;

namespace AiChatAssistant.Api.Tests;

public class VerificationOnFactory : ApiFactory { public VerificationOnFactory() { Settings["Account:RequireEmailVerification"] = "true"; } }

/// <summary>Phase 22 through the real pipeline: what a person's other sessions and a deleted account's token do, and the verification mode end to end.</summary>
public class AccountPipelineTests : IClassFixture<ApiFactory>
{
    private const string Password = "first-password-1";
    private readonly ApiFactory _factory;

    public AccountPipelineTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0); // creates the in-memory database and seeds the roles registration needs
    }

    private static int _seq;
    private static string NewEmail() => $"acct{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private async Task<(string Email, string Token)> RegisterAsync(HttpClient client)
    {
        var email = NewEmail();
        var response = await client.PostAsJsonAsync("/api/auth/register", new { name = "Acct Person", email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (email, (await Json(response)).GetProperty("token").GetString()!);
    }

    private async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpStatusCode> CallWith(string token) =>
        (await _factory.CreateClient().WithToken(token).GetAsync("/api/chat/sessions")).StatusCode;

    // A token issued in the same whole second as the change is not distinguishable from one issued after it, so tests that
    // need "an older session" let the clock move on first (the real-world gap is a person's sessions made over seconds or longer).
    private static Task NextSecond() => Task.Delay(1100);

    [Theory]
    [InlineData("GET", "/api/account")]
    [InlineData("PUT", "/api/account/profile")]
    [InlineData("POST", "/api/account/change-password")]
    [InlineData("POST", "/api/account/delete")]
    public async Task EveryAccountEndpoint_RequiresASignedInCaller_InTheStandardShape(string method, string path)
    {
        var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await Http.CodeOf(response));
    }

    [Fact]
    public async Task TheProfile_ShowsTheCallersName_AndAnUpdateIsVisibleAtOnce()
    {
        var client = _factory.CreateClient();
        var (email, token) = await RegisterAsync(client);
        var authed = _factory.CreateClient().WithToken(token);

        var put = await authed.PutAsJsonAsync("/api/account/profile", new { name = "Renamed Person" });
        var get = await Json(await authed.GetAsync("/api/account"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Renamed Person", get.GetProperty("name").GetString());
        Assert.Equal(email, get.GetProperty("email").GetString());
        Assert.True(get.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, await CallWith(token)); // renaming does not touch the session
    }

    [Fact]
    public async Task ChangingThePassword_KillsEveryOlderSession_KeepsTheCurrentOne_AndTheNewPasswordWorks()
    {
        var client = _factory.CreateClient();
        var (email, tokenA) = await RegisterAsync(client);
        var tokenB = await LoginAsync(client, email, Password);   // a second session, e.g. another device
        await NextSecond();

        var change = await _factory.CreateClient().WithToken(tokenA).PostAsJsonAsync("/api/account/change-password", new { currentPassword = Password, newPassword = "second-password-2" });
        var fresh = (await Json(change)).GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallWith(tokenA));  // even the session that changed it, until it uses the new token
        Assert.Equal(HttpStatusCode.Unauthorized, await CallWith(tokenB));  // the other device is signed out
        Assert.Equal(HttpStatusCode.OK, await CallWith(fresh));             // the response's token keeps this session going
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password })).StatusCode);
        await LoginAsync(client, email, "second-password-2");
    }

    [Fact]
    public async Task AnOlderSession_CannotOpenTheRealtimeHubEither()
    {
        var client = _factory.CreateClient();
        var (_, oldToken) = await RegisterAsync(client);
        await NextSecond();
        var fresh = (await Json(await _factory.CreateClient().WithToken(oldToken).PostAsJsonAsync("/api/account/change-password", new { currentPassword = Password, newPassword = "second-password-2" }))).GetProperty("token").GetString()!;

        var withOld = await client.PostAsync($"/hubs/direct/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(oldToken)}", null);
        var withNew = await client.PostAsync($"/hubs/direct/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(fresh)}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact]
    public async Task AWrongCurrentPassword_ChangesNothing_AndTheSessionKeepsWorking()
    {
        var (email, token) = await RegisterAsync(_factory.CreateClient());

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync("/api/account/change-password", new { currentPassword = "not-it", newPassword = "second-password-2" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_current_password", await Http.CodeOf(response));
        Assert.Equal(HttpStatusCode.OK, await CallWith(token));
        await LoginAsync(_factory.CreateClient(), email, Password);
    }

    [Fact]
    public async Task ResettingAForgottenPassword_AlsoSignsOutOlderSessions()
    {
        var client = _factory.CreateClient();
        var (email, token) = await RegisterAsync(client);
        var raw = "reset-" + Guid.NewGuid().ToString("N");
        _factory.WithDb(db =>
        {
            var user = db.Users.Single(u => u.Email == email);
            db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            db.SaveChanges();
            return 0;
        });
        await NextSecond();

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { token = raw, newPassword = "after-reset-pass-3" });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallWith(token));
    }

    [Fact]
    public async Task DeletingTheAccount_Is204_TheTokenStopsWorkingAtOnce_AndTheEmailCannotLogInAgain()
    {
        var client = _factory.CreateClient();
        var (email, token) = await RegisterAsync(client);
        var (_, other) = await RegisterAsync(client);

        var delete = await _factory.CreateClient().WithToken(token).PostAsJsonAsync("/api/account/delete", new { password = Password });

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallWith(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await CallWith(other)); // nobody else is affected
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { name = "Back Again", email, password = Password })).StatusCode); // the address is free again
    }

    [Fact]
    public async Task DeletingWithAWrongPassword_Is400_AndNothingIsDeleted()
    {
        var (email, token) = await RegisterAsync(_factory.CreateClient());

        var delete = await _factory.CreateClient().WithToken(token).PostAsJsonAsync("/api/account/delete", new { password = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await CallWith(token));
        await LoginAsync(_factory.CreateClient(), email, Password);
    }

    [Fact]
    public async Task TheOnlyAdmin_CannotDeleteThemselves()
    {
        var (admin, token) = _factory.CreateUser("Only Admin", NewEmail(), "Admin");
        _factory.WithDb(db => { db.Users.Single(u => u.Id == admin.Id).PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4); db.SaveChanges(); return 0; });

        var delete = await _factory.CreateClient().WithToken(token).PostAsJsonAsync("/api/account/delete", new { password = Password });

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("last_admin", await Http.CodeOf(delete));
    }

    [Fact]
    public async Task ADeletedPersonsDirectConversationsAreGoneFromThePeer_AtTheApiLevel()
    {
        var client = _factory.CreateClient();
        var (_, aliceToken) = await RegisterAsync(client);
        var (_, bobToken) = await RegisterAsync(client);
        var bobId = _factory.WithDb(db => db.Users.OrderByDescending(u => u.Id).First().Id);
        var start = await _factory.CreateClient().WithToken(aliceToken).PostAsJsonAsync("/api/direct/conversations", new { userId = bobId });
        Assert.True(start.IsSuccessStatusCode);

        await _factory.CreateClient().WithToken(bobToken).PostAsJsonAsync("/api/account/delete", new { password = Password });

        // In memory only the user row goes (the cascade is the database's job and is proved on real MySQL in the Docker run), but the
        // peer must at least never be able to reach the deleted person any more.
        var search = await _factory.CreateClient().WithToken(aliceToken).GetAsync("/api/direct/users?search=Acct");
        Assert.DoesNotContain($"\"id\":{bobId},", await search.Content.ReadAsStringAsync());
    }
}

public class AccountRateLimitTests : IClassFixture<LowAuthLimitFactory>
{
    private readonly LowAuthLimitFactory _factory;
    public AccountRateLimitTests(LowAuthLimitFactory factory) { _factory = factory; _factory.WithDb(_ => 0); }

    [Theory]
    [InlineData("/api/account/change-password", "{\"currentPassword\":\"x\",\"newPassword\":\"long-enough-1\"}")]
    [InlineData("/api/account/delete", "{\"password\":\"x\"}")]
    public async Task ThePasswordSensitiveEndpoints_ShareTheAddressesAuthLimit(string path, string body)
    {
        var (_, token) = _factory.CreateUser("Limited", $"limited-{Guid.NewGuid():N}@example.com", "User");
        HttpRequestMessage Req()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            r.Headers.Add("X-Test-Remote-Ip", "10.7.0." + Math.Abs(path.GetHashCode() % 200));
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return r;
        }
        var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(Req())).StatusCode);

        var blocked = await client.SendAsync(Req());

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}

/// <summary>Account:RequireEmailVerification on, end to end through HTTP.</summary>
public class VerificationPipelineTests : IClassFixture<VerificationOnFactory>
{
    private readonly VerificationOnFactory _factory;
    private readonly List<(string Email, string Link)> _sent = new();

    public VerificationPipelineTests(VerificationOnFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0);
        _factory.Email.Reset();
        _factory.Email.Setup(e => e.EnqueueEmailVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((to, _, link, _) => _sent.Add((to, link))).Returns(Task.CompletedTask);
    }

    private static string NewEmail() => $"verify-{Guid.NewGuid():N}@example.com";
    private static string TokenIn(string link) => Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

    private Task<HttpResponseMessage> Register(string email) =>
        _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { name = "Verify Person", email, password = "long-enough-1" });

    [Fact]
    public async Task RegisteringIs202_WithNoToken_AndTheSameBodyForANewAndAnExistingAddress()
    {
        var email = NewEmail();

        var first = await Register(email);
        var second = await Register(email);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        Assert.Equal(body, await second.Content.ReadAsStringAsync());
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verificationRequired", body);
    }

    [Fact]
    public async Task UntilTheLinkIsUsed_LoggingInIs403_ThenItWorks()
    {
        var email = NewEmail();
        await Register(email);
        var client = _factory.CreateClient();

        var before = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "long-enough-1" });
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = TokenIn(_sent.Single(s => s.Email == email).Link) });
        var after = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "long-enough-1" });

        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal("email_not_verified", await Http.CodeOf(before));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ALinkWorksOnce_AndAFreshRequestMakesTheOldLinkUseless()
    {
        var email = NewEmail();
        await Register(email);
        var client = _factory.CreateClient();
        var oldLink = _sent.Single(s => s.Email == email).Link;

        var resend = await client.PostAsJsonAsync("/api/auth/resend-verification", new { email });
        var newLink = _sent.Last(s => s.Email == email).Link;
        var oldUse = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = TokenIn(oldLink) });
        var newUse = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = TokenIn(newLink) });
        var again = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = TokenIn(newLink) });

        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.NotEqual(oldLink, newLink);
        Assert.Equal(HttpStatusCode.BadRequest, oldUse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newUse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Resend_GivesOneAnswer_ForUnknownAndRegisteredAddresses()
    {
        var email = NewEmail();
        await Register(email);
        var client = _factory.CreateClient();

        var known = await client.PostAsJsonAsync("/api/auth/resend-verification", new { email });
        var unknown = await client.PostAsJsonAsync("/api/auth/resend-verification", new { email = NewEmail() });

        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnconfirmedAccount_CannotUseAnythingBehindALogin_BecauseItNeverGetsAToken()
    {
        var response = await Register(NewEmail());

        Assert.False((await response.Content.ReadAsStringAsync()).Contains("eyJ")); // no JWT anywhere in the body
    }
}
