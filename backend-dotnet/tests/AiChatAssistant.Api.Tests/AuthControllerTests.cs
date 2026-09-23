using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AiChatAssistant.Api.Tests;

public class AuthControllerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<ITokenService> _tokenService = new();

    public AuthControllerTests()
    {
        _tokenService
            .Setup(s => s.CreateToken(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(("fake-jwt", DateTime.UtcNow.AddHours(1)));
    }

    public void Dispose() => _db.Dispose();

    private AuthController CreateController() =>
        new(_db, _tokenService.Object, new RegisterRequestValidator(), new LoginRequestValidator());

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
}
