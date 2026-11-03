using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Tests.TestSupport;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 23 through the real pipeline: short-lived access tokens, rotating refresh tokens, logout, and revocation on password changes.</summary>
public class RefreshPipelineTests : IClassFixture<ApiFactory>
{
    private const string Password = "first-password-1";
    private readonly ApiFactory _factory;

    public RefreshPipelineTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0);
    }

    private static string NewEmail() => $"ref-{Guid.NewGuid():N}@example.com";
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private record Pair(string Email, string Access, string Refresh);

    private async Task<Pair> RegisterAsync()
    {
        var email = NewEmail();
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { name = "Ref Person", email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        return new Pair(email, body.GetProperty("token").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<Pair> LoginAsync(string email, string password = Password)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        return new Pair(email, body.GetProperty("token").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

    private async Task<HttpStatusCode> CallWith(string access) =>
        (await _factory.CreateClient().WithToken(access).GetAsync("/api/chat/sessions")).StatusCode;

    private static Task NextSecond() => Task.Delay(1100);

    [Fact]
    public async Task RegisterAndLogin_BothReturnARefreshToken_AndTheAccessTokenLivesAboutFifteenMinutes()
    {
        var registered = await RegisterAsync();
        var loggedIn = await LoginAsync(registered.Email);

        Assert.False(string.IsNullOrWhiteSpace(registered.Refresh));
        Assert.NotEqual(registered.Refresh, loggedIn.Refresh);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(loggedIn.Access);
        var lifetime = jwt.ValidTo - DateTime.UtcNow;
        Assert.InRange(lifetime.TotalMinutes, 14, 16);
    }

    [Fact]
    public async Task Refresh_ReturnsANewWorkingPair_AndTheOldRefreshTokenIsDead()
    {
        var pair = await RegisterAsync();

        var response = await RefreshAsync(pair.Refresh);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        var newAccess = body.GetProperty("token").GetString()!;
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(pair.Refresh, newRefresh);
        Assert.Equal(pair.Email, body.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, await CallWith(newAccess));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(pair.Refresh)).StatusCode);
    }

    [Fact]
    public async Task ReusingARotatedToken_AfterTheGraceWindow_EndsTheLoginForTheThiefAndTheOwner()
    {
        var pair = await RegisterAsync();
        var newRefresh = (await Json(await RefreshAsync(pair.Refresh))).GetProperty("refreshToken").GetString()!;
        // The grace window is 10 s; move the stored rotation time back instead of sleeping that long.
        _factory.WithDb(db =>
        {
            foreach (var t in db.RefreshTokens.Where(t => t.UsedAt != null)) t.UsedAt = DateTime.UtcNow.AddMinutes(-5);
            db.SaveChanges();
            return 0;
        });

        var replay = await RefreshAsync(pair.Refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_refresh_token", await Http.CodeOf(replay));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(newRefresh)).StatusCode);
    }

    [Fact]
    public async Task TwoTabsRefreshingAtOnce_OnlyOneWins_AndTheWinnersSessionSurvives()
    {
        var pair = await RegisterAsync();

        var first = await RefreshAsync(pair.Refresh);
        var second = await RefreshAsync(pair.Refresh); // arrives a moment later: inside the grace window

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        var winnerRefresh = (await Json(first)).GetProperty("refreshToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(winnerRefresh)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("definitely-not-a-token")]
    public async Task Refresh_WithAnEmptyOrUnknownToken_Is401_InTheStandardShape(string token)
    {
        var response = await RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_refresh_token", await Http.CodeOf(response));
    }

    [Fact]
    public async Task Refresh_WithAnOversizedToken_IsRefusedWithoutLookingItUp()
    {
        var response = await RefreshAsync(new string('a', 5000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_NeedsNoAccessToken_SoAnExpiredOneCanStillBeExchanged()
    {
        var pair = await RegisterAsync();

        var response = await _factory.CreateClient().WithToken("an.expired.token").PostAsJsonAsync("/api/auth/refresh", new { refreshToken = pair.Refresh });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken_ButLeavesOtherDevicesSignedIn()
    {
        var phone = await RegisterAsync();
        var laptop = await LoginAsync(phone.Email);

        var logout = await _factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new { refreshToken = phone.Refresh });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(laptop.Refresh)).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"refreshToken\":\"never-issued\"}")]
    [InlineData("{\"refreshToken\":null}")]
    public async Task Logout_IsIdempotent_AndNeverFailsForAnUnknownToken(string body)
    {
        var response = await _factory.CreateClient().PostAsync("/api/auth/logout", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Logout_AfterANormalRefresh_StillEndsTheLogin_EvenWithTheOldToken()
    {
        var pair = await RegisterAsync();
        var newRefresh = (await Json(await RefreshAsync(pair.Refresh))).GetProperty("refreshToken").GetString()!;

        await _factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new { refreshToken = newRefresh });

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(newRefresh)).StatusCode);
    }

    [Fact]
    public async Task ChangingThePassword_EndsEveryOtherLogin_AndKeepsTheCurrentOneWithItsNewPair()
    {
        var current = await RegisterAsync();
        var other = await LoginAsync(current.Email);
        await NextSecond();

        var change = await _factory.CreateClient().WithToken(current.Access)
            .PostAsJsonAsync("/api/account/change-password", new { currentPassword = Password, newPassword = "second-password-2" });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var body = await Json(change);
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(newRefresh));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(current.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(other.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(newRefresh)).StatusCode);
    }

    [Fact]
    public async Task ResettingAForgottenPassword_EndsEveryLogin()
    {
        var pair = await RegisterAsync();
        var raw = "reset-" + Guid.NewGuid().ToString("N");
        _factory.WithDb(db =>
        {
            var user = db.Users.Single(u => u.Email == pair.Email);
            db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            db.SaveChanges();
            return 0;
        });

        var reset = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = raw, newPassword = "after-reset-pass-3" });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(pair.Refresh)).StatusCode);
    }

    [Fact]
    public async Task LogoutAll_NeedsSignIn_AndEndsEveryDeviceIncludingLiveAccessTokens()
    {
        var phone = await RegisterAsync();
        var laptop = await LoginAsync(phone.Email);
        await NextSecond();

        var anonymous = await _factory.CreateClient().PostAsync("/api/auth/logout-all", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var all = await _factory.CreateClient().WithToken(phone.Access).PostAsync("/api/auth/logout-all", null);

        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(laptop.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallWith(laptop.Access));
        // Logging in again works.
        Assert.Equal(HttpStatusCode.OK, await CallWith((await LoginAsync(phone.Email)).Access));
    }

    [Fact]
    public async Task DeletingTheAccount_MakesItsRefreshTokensUseless()
    {
        var pair = await RegisterAsync();

        var delete = await _factory.CreateClient().WithToken(pair.Access).PostAsJsonAsync("/api/account/delete", new { password = Password });

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(pair.Refresh)).StatusCode);
    }

    [Fact]
    public async Task ARefreshToken_IsNeverStoredInPlainText()
    {
        var pair = await RegisterAsync();

        var stored = _factory.WithDb(db => db.RefreshTokens.Select(t => t.TokenHash).ToList());

        Assert.DoesNotContain(pair.Refresh, stored);
    }

    [Fact]
    public async Task ARefreshToken_CannotBeUsedAsAnAccessToken()
    {
        var pair = await RegisterAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await CallWith(pair.Refresh));
    }
}
