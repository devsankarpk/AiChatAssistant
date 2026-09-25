using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AiChatAssistant.Api.Tests;

public class AuthControllerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<ITokenService> _tokenService = new();
    private readonly Mock<IEmailSender> _emailSender = new();
    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    public AuthControllerTests()
    {
        _tokenService
            .Setup(s => s.CreateToken(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(("fake-jwt", DateTime.UtcNow.AddHours(1)));
    }

    public void Dispose() => _db.Dispose();

    private AuthController CreateController() =>
        new(
            _db,
            _tokenService.Object,
            _emailSender.Object,
            _configuration,
            new RegisterRequestValidator(),
            new LoginRequestValidator(),
            new ForgotPasswordRequestValidator(),
            new ResetPasswordRequestValidator());

    [Fact]
    public async Task Register_CreatesUser_AssignsDefaultUserRole_AndReturnsToken()
    {
        var controller = CreateController();
        var request = new RegisterRequest { Name = "Ada", Email = "Ada@Example.com", Password = "Passw0rd!" };

        var result = await controller.Register(request, CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<AuthResponse>(created.Value);
        Assert.Equal("fake-jwt", body.Token);
        Assert.Equal(["User"], body.User.Roles);

        // Email is normalized to lowercase for storage/lookup.
        var stored = Assert.Single(_db.Users);
        Assert.Equal("ada@example.com", stored.Email);
        Assert.NotEqual("Passw0rd!", stored.PasswordHash); // never stored in plain text
    }

    [Fact]
    public async Task Register_ReturnsConflict_WhenEmailAlreadyTaken()
    {
        var controller = CreateController();
        var request = new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" };
        await controller.Register(request, CancellationToken.None);

        var second = await controller.Register(
            new RegisterRequest { Name = "Someone Else", Email = "ADA@example.com", Password = "Passw0rd!" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Single(_db.Users); // the second registration never got persisted
    }

    [Fact]
    public async Task Register_Returns400_ForWeakPassword()
    {
        var controller = CreateController();

        var result = await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "short" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_db.Users);
    }

    [Fact]
    public async Task Login_ReturnsToken_ForCorrectCredentials()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);

        var result = await controller.Login(new LoginRequest { Email = "ada@example.com", Password = "Passw0rd!" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("fake-jwt", body.Token);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_ForWrongPassword()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);

        var result = await controller.Login(new LoginRequest { Email = "ada@example.com", Password = "WrongPassword!" }, CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_ForUnknownEmail()
    {
        var controller = CreateController();

        var result = await controller.Login(new LoginRequest { Email = "nobody@example.com", Password = "Passw0rd!" }, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public void Me_ReturnsIdEmailAndRoles_FromTheCallersClaims()
    {
        var controller = CreateController();
        controller.SetUser(ClaimsPrincipalFactory.Create(7, "someone@example.com", "Admin", "User"));

        var result = controller.Me();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<MeResponse>(ok.Value);
        Assert.Equal(7, body.Id);
        Assert.Equal("someone@example.com", body.Email);
        Assert.Equal(["Admin", "User"], body.Roles);
    }

    [Fact]
    public async Task ForgotPassword_SendsResetLink_ForKnownEmail()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);

        var result = await controller.ForgotPassword(new ForgotPasswordRequest { Email = "ADA@example.com" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _emailSender.Verify(
            e => e.SendPasswordResetEmailAsync("ada@example.com", "Ada", It.Is<string>(link => link.Contains("/reset-password?token=")), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Single(_db.PasswordResetTokens);
    }

    [Fact]
    public async Task ForgotPassword_ReturnsTheSameGenericMessage_ForUnknownEmail_AndSendsNothing()
    {
        var controller = CreateController();

        var known = await controller.ForgotPassword(new ForgotPasswordRequest { Email = "nobody@example.com" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(known.Result);
        Assert.Equal(
            "If that email is registered, a password reset link has been sent.",
            Assert.IsType<AuthMessageResponse>(ok.Value).Message);
        _emailSender.Verify(
            e => e.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ForgotPassword_InvalidatesAnyEarlierUnusedToken_WhenRequestedAgain()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);

        await controller.ForgotPassword(new ForgotPasswordRequest { Email = "ada@example.com" }, CancellationToken.None);
        await controller.ForgotPassword(new ForgotPasswordRequest { Email = "ada@example.com" }, CancellationToken.None);

        Assert.Single(_db.PasswordResetTokens); // the first was removed, not left dangling
    }

    [Fact]
    public async Task ResetPassword_ChangesThePassword_AndTheOldPasswordNoLongerWorks()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);
        var token = await CaptureResetToken(controller, "ada@example.com");

        var result = await controller.ResetPassword(
            new ResetPasswordRequest { Token = token, NewPassword = "NewPassw0rd!" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        var oldPasswordLogin = await controller.Login(new LoginRequest { Email = "ada@example.com", Password = "Passw0rd!" }, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(oldPasswordLogin.Result);

        var newPasswordLogin = await controller.Login(new LoginRequest { Email = "ada@example.com", Password = "NewPassw0rd!" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(newPasswordLogin.Result);
    }

    [Fact]
    public async Task ResetPassword_Returns400_ForAnUnknownToken()
    {
        var controller = CreateController();

        var result = await controller.ResetPassword(
            new ResetPasswordRequest { Token = "not-a-real-token", NewPassword = "NewPassw0rd!" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_Returns400_WhenTheSameTokenIsUsedTwice()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);
        var token = await CaptureResetToken(controller, "ada@example.com");

        await controller.ResetPassword(new ResetPasswordRequest { Token = token, NewPassword = "NewPassw0rd!" }, CancellationToken.None);
        var secondAttempt = await controller.ResetPassword(
            new ResetPasswordRequest { Token = token, NewPassword = "AnotherPassw0rd!" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(secondAttempt.Result);
    }

    [Fact]
    public async Task ResetPassword_Returns400_ForAnExpiredToken()
    {
        var controller = CreateController();
        await controller.Register(
            new RegisterRequest { Name = "Ada", Email = "ada@example.com", Password = "Passw0rd!" },
            CancellationToken.None);
        var token = await CaptureResetToken(controller, "ada@example.com");

        // Simulate time passing rather than waiting a real hour out.
        var stored = Assert.Single(_db.PasswordResetTokens);
        stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var result = await controller.ResetPassword(new ResetPasswordRequest { Token = token, NewPassword = "NewPassw0rd!" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>Drives ForgotPassword and pulls the raw token out of the link the (mocked)
    /// email would have carried - there's no other way to get it, by design (only its hash
    /// is ever persisted).</summary>
    private async Task<string> CaptureResetToken(AuthController controller, string email)
    {
        string? capturedLink = null;
        _emailSender
            .Setup(e => e.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, link, _) => capturedLink = link)
            .Returns(Task.CompletedTask);

        await controller.ForgotPassword(new ForgotPasswordRequest { Email = email }, CancellationToken.None);

        Assert.NotNull(capturedLink);
        return capturedLink!.Split("token=")[1];
    }
}
