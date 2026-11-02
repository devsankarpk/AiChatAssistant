using System.Security.Claims;
using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Account;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Security;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 22: the signed-in person's own account (profile, password, delete), with the real validators and an in-memory database.</summary>
public class AccountControllerTests : IDisposable
{
    private const string Password = "current-password-1";
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<ITokenService> _tokens = new();
    private readonly InMemoryRateLimitCounter _counter = new(TimeProvider.System);
    private readonly User _ada;
    private readonly User _bob;

    public AccountControllerTests()
    {
        _tokens.Setup(t => t.CreateToken(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>())).Returns(("fresh-jwt", DateTime.UtcNow.AddHours(1)));
        _ada = AddUser("Ada", "ada@example.com", Role.User);
        _bob = AddUser("Bob", "bob@example.com", Role.User);
    }

    public void Dispose() => _db.Dispose();

    private User AddUser(string name, string email, string role)
    {
        var user = new User { Name = name, Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4), EmailVerifiedAt = DateTime.UtcNow };
        user.UserRoles.Add(new UserRole { Role = _db.Roles.Single(r => r.Name == role) });
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private AccountController For(User user, LockoutOptions? lockout = null)
    {
        var controller = new AccountController(
            _db, _tokens.Object,
            new LoginLockout(_counter, Options.Create(lockout ?? new LockoutOptions())),
            new UpdateProfileRequestValidator(), new ChangePasswordRequestValidator(), new DeleteAccountRequestValidator(),
            NullLogger<AccountController>.Instance);
        controller.SetUser(ClaimsPrincipalFactory.Create(user.Id, user.Email, user.UserRoles.Select(ur => ur.Role.Name).ToArray()));
        controller.ControllerContext.HttpContext = new DefaultHttpContext { User = controller.ControllerContext.HttpContext.User };
        return controller;
    }

    private static string CodeOf(IActionResult result) => ((Common.ErrorResponse)((ObjectResult)result).Value!).Error.Code;
    private static string CodeOf<T>(ActionResult<T> result) => ((Common.ErrorResponse)((ObjectResult)result.Result!).Value!).Error.Code;

    // ---- profile ----

    [Fact]
    public async Task GetProfile_ReturnsTheCallersOwnDetails_NotAnyoneElses()
    {
        var result = await For(_ada).GetProfile(CancellationToken.None);

        var profile = Assert.IsType<ProfileResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Ada", profile.Name);
        Assert.Equal("ada@example.com", profile.Email);
        Assert.Equal(new[] { "User" }, profile.Roles);
        Assert.True(profile.EmailVerified);
    }

    [Fact]
    public async Task UpdateProfile_ChangesOnlyTheCallersName_TrimmedAndStored()
    {
        var result = await For(_ada).UpdateProfile(new UpdateProfileRequest { Name = "  Ada Lovelace  " }, CancellationToken.None);

        Assert.Equal("Ada Lovelace", Assert.IsType<ProfileResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Name);
        Assert.Equal("Ada Lovelace", _db.Users.Single(u => u.Id == _ada.Id).Name);
        Assert.Equal("Bob", _db.Users.Single(u => u.Id == _bob.Id).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateProfile_ABlankName_Is400_AndNothingChanges(string name)
    {
        var result = await For(_ada).UpdateProfile(new UpdateProfileRequest { Name = name }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Ada", _db.Users.Single(u => u.Id == _ada.Id).Name);
    }

    [Fact]
    public async Task UpdateProfile_TheNameLimitIs120()
    {
        Assert.IsType<OkObjectResult>((await For(_ada).UpdateProfile(new UpdateProfileRequest { Name = new string('a', 120) }, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await For(_ada).UpdateProfile(new UpdateProfileRequest { Name = new string('a', 121) }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ATokenForAnAccountThatNoLongerExists_GetsTheStandard401Shape()
    {
        var ghost = new User { Id = 9999, Name = "Ghost", Email = "ghost@example.com" };

        var result = await For(ghost).GetProfile(CancellationToken.None);

        Assert.Equal(401, ((ObjectResult)result.Result!).StatusCode);
        Assert.Equal("unauthorized", CodeOf(result));
    }

    // ---- change password ----

    [Fact]
    public async Task ChangePassword_WithTheRightCurrentPassword_ChangesIt_StampsTheTime_AndReturnsAFreshToken()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await For(_ada).ChangePassword(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "a-brand-new-password" }, CancellationToken.None);

        var body = Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("fresh-jwt", body.Token);
        var stored = _db.Users.Single(u => u.Id == _ada.Id);
        Assert.False(BCrypt.Net.BCrypt.Verify(Password, stored.PasswordHash));
        Assert.True(BCrypt.Net.BCrypt.Verify("a-brand-new-password", stored.PasswordHash));
        Assert.NotNull(stored.PasswordChangedAt);
        Assert.True(stored.PasswordChangedAt >= before);
    }

    [Fact]
    public async Task ChangePassword_WithAWrongCurrentPassword_Is400_InvalidCurrentPassword_AndNothingChanges()
    {
        var result = await For(_ada).ChangePassword(new ChangePasswordRequest { CurrentPassword = "not-it", NewPassword = "a-brand-new-password" }, CancellationToken.None);

        Assert.Equal(400, ((ObjectResult)result.Result!).StatusCode);
        Assert.Equal("invalid_current_password", CodeOf(result));
        var stored = _db.Users.Single(u => u.Id == _ada.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(Password, stored.PasswordHash));
        Assert.Null(stored.PasswordChangedAt);
        _tokens.Verify(t => t.CreateToken(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_ToTheSamePassword_Is400_PasswordUnchanged()
    {
        var result = await For(_ada).ChangePassword(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = Password }, CancellationToken.None);

        Assert.Equal("password_unchanged", CodeOf(result));
        Assert.Null(_db.Users.Single(u => u.Id == _ada.Id).PasswordChangedAt);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task ChangePassword_ATooShortOrEmptyNewPassword_Is400(string newPassword)
    {
        var result = await For(_ada).ChangePassword(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = newPassword }, CancellationToken.None);

        Assert.Equal("validation_error", CodeOf(result));
    }

    [Fact]
    public async Task ChangePassword_ATooLongNewPassword_Is400()
    {
        var result = await For(_ada).ChangePassword(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = new string('x', 129) }, CancellationToken.None);

        Assert.Equal("validation_error", CodeOf(result));
    }

    [Fact]
    public async Task ChangePassword_RepeatedWrongCurrentPasswords_LockTheAction_EvenForTheRightOne()
    {
        var controller = For(_ada, new LockoutOptions { MaxFailedAttempts = 3, LockoutMinutes = 15 });
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal("invalid_current_password", CodeOf(await controller.ChangePassword(new ChangePasswordRequest { CurrentPassword = "nope", NewPassword = "a-brand-new-password" }, CancellationToken.None)));
        }

        var third = await controller.ChangePassword(new ChangePasswordRequest { CurrentPassword = "nope", NewPassword = "a-brand-new-password" }, CancellationToken.None);
        var right = await controller.ChangePassword(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "a-brand-new-password" }, CancellationToken.None);

        Assert.Equal(423, ((ObjectResult)third.Result!).StatusCode);
        Assert.Equal(423, ((ObjectResult)right.Result!).StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(Password, _db.Users.Single(u => u.Id == _ada.Id).PasswordHash));
    }

    [Fact]
    public async Task ChangePassword_AFailureForOnePerson_DoesNotLockAnotherPersonsChange()
    {
        var lockout = new LockoutOptions { MaxFailedAttempts = 1 };
        await For(_ada, lockout).ChangePassword(new ChangePasswordRequest { CurrentPassword = "nope", NewPassword = "a-brand-new-password" }, CancellationToken.None);

        var bobs = await For(_bob, lockout).ChangePassword(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "a-brand-new-password" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(bobs.Result);
    }

    // ---- delete ----

    [Fact]
    public async Task DeleteAccount_WithTheRightPassword_RemovesTheUser_And204()
    {
        var result = await For(_ada).DeleteAccount(new DeleteAccountRequest { Password = Password }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(_db.Users, u => u.Id == _ada.Id);
        Assert.Contains(_db.Users, u => u.Id == _bob.Id);
    }

    [Fact]
    public async Task DeleteAccount_WithAWrongPassword_Is400_AndTheAccountStays()
    {
        var result = await For(_ada).DeleteAccount(new DeleteAccountRequest { Password = "nope" }, CancellationToken.None);

        Assert.Equal("invalid_current_password", CodeOf(result));
        Assert.Contains(_db.Users, u => u.Id == _ada.Id);
    }

    [Fact]
    public async Task DeleteAccount_WithNoPassword_Is400()
    {
        var result = await For(_ada).DeleteAccount(new DeleteAccountRequest { Password = "" }, CancellationToken.None);

        Assert.Equal("validation_error", CodeOf(result));
        Assert.Contains(_db.Users, u => u.Id == _ada.Id);
    }

    [Fact]
    public async Task DeleteAccount_TheOnlyAdminCannotDeleteThemselves_409()
    {
        var admin = AddUser("Root", "root@example.com", Role.Admin);

        var result = await For(admin).DeleteAccount(new DeleteAccountRequest { Password = Password }, CancellationToken.None);

        Assert.Equal(409, ((ObjectResult)result).StatusCode);
        Assert.Equal("last_admin", CodeOf(result));
        Assert.Contains(_db.Users, u => u.Id == admin.Id);
    }

    [Fact]
    public async Task DeleteAccount_AnAdminMayDeleteThemselves_WhenAnotherAdminExists()
    {
        var admin = AddUser("Root", "root@example.com", Role.Admin);
        AddUser("Second", "second@example.com", Role.Admin);

        var result = await For(admin).DeleteAccount(new DeleteAccountRequest { Password = Password }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteAccount_RepeatedWrongPasswords_Lock_SoASessionCannotBeUsedToGuess()
    {
        var controller = For(_ada, new LockoutOptions { MaxFailedAttempts = 2 });
        await controller.DeleteAccount(new DeleteAccountRequest { Password = "no" }, CancellationToken.None);
        await controller.DeleteAccount(new DeleteAccountRequest { Password = "no" }, CancellationToken.None);

        var right = await controller.DeleteAccount(new DeleteAccountRequest { Password = Password }, CancellationToken.None);

        Assert.Equal(423, ((ObjectResult)right).StatusCode);
        Assert.Contains(_db.Users, u => u.Id == _ada.Id);
    }

    [Fact]
    public async Task WrongPasswordsHere_AreNotCountedAsLoginFailures_AndViceVersa()
    {
        var lockout = new LockoutOptions { MaxFailedAttempts = 1 };
        await For(_ada, lockout).DeleteAccount(new DeleteAccountRequest { Password = "no" }, CancellationToken.None);

        var loginState = await new LoginLockout(_counter, Options.Create(lockout)).CheckAsync("ada@example.com");

        Assert.False(loginState.Locked);
    }
}

/// <summary>The per-request check that makes a password change (and a deleted account) take effect on tokens that are still validly signed.</summary>
public class AccountTokenValidatorTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly User _user;

    public AccountTokenValidatorTests()
    {
        _user = new User { Name = "A", Email = "a@example.com", PasswordHash = "x" };
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private ClaimsPrincipal Token(int? userId = null, long? issuedAt = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, (userId ?? _user.Id).ToString()) };
        if (issuedAt is not null) claims.Add(new Claim("iat", issuedAt.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static long Seconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    [Fact]
    public async Task AnAccountThatNeverChangedItsPassword_AcceptsAnyToken_EvenOneWithoutAnIssueTime()
    {
        Assert.Null(await AccountTokenValidator.ValidateAsync(_db, Token(), CancellationToken.None));
        Assert.Null(await AccountTokenValidator.ValidateAsync(_db, Token(issuedAt: 1), CancellationToken.None));
    }

    [Fact]
    public async Task ADeletedAccount_IsRefused()
    {
        var reason = await AccountTokenValidator.ValidateAsync(_db, Token(userId: 424242), CancellationToken.None);

        Assert.Contains("no longer exists", reason);
    }

    [Fact]
    public async Task ATokenWithNoUser_IsRefused()
    {
        var reason = await AccountTokenValidator.ValidateAsync(_db, new ClaimsPrincipal(new ClaimsIdentity()), CancellationToken.None);

        Assert.NotNull(reason);
    }

    [Fact]
    public async Task ATokenIssuedBeforeThePasswordChange_IsRefused()
    {
        var changed = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        _user.PasswordChangedAt = changed;
        _db.SaveChanges();

        Assert.NotNull(await AccountTokenValidator.ValidateAsync(_db, Token(issuedAt: Seconds(changed) - 1), CancellationToken.None));
    }

    [Fact]
    public async Task ATokenIssuedAtOrAfterThePasswordChange_IsAccepted()
    {
        var changed = new DateTime(2026, 11, 2, 12, 0, 0, 500, DateTimeKind.Utc);
        _user.PasswordChangedAt = changed;
        _db.SaveChanges();

        Assert.Null(await AccountTokenValidator.ValidateAsync(_db, Token(issuedAt: Seconds(changed)), CancellationToken.None));
        Assert.Null(await AccountTokenValidator.ValidateAsync(_db, Token(issuedAt: Seconds(changed) + 3600), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public async Task AfterAPasswordChange_ATokenWithoutAReadableIssueTime_IsRefused(string? iat)
    {
        _user.PasswordChangedAt = DateTime.UtcNow.AddMinutes(-5);
        _db.SaveChanges();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, _user.Id.ToString()) };
        if (iat is not null) claims.Add(new Claim("iat", iat));

        Assert.NotNull(await AccountTokenValidator.ValidateAsync(_db, new ClaimsPrincipal(new ClaimsIdentity(claims, "t")), CancellationToken.None));
    }

    [Fact]
    public void ATokenFromTheRealTokenService_CarriesAnIssueTimeInWholeSeconds()
    {
        var service = new TokenService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key" }).Build());

        var (token, _) = service.CreateToken(_user, new[] { "User" });

        var iat = long.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type == "iat").Value);
        Assert.InRange(iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
    }
}
