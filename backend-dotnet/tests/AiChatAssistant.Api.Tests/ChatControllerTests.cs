using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// Per CLAUDE.md's testing expectations: mocks IPythonAiClient rather than calling a real LLM.
/// Uses the real FluentValidation validators (no external deps of their own) and an in-memory
/// AppDbContext, so these exercise actual controller + validation + persistence logic - only the
/// network boundary to Python is faked.
/// </summary>
public class ChatControllerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IPythonAiClient> _pythonClient = new();
    private readonly User _owner;
    private readonly User _otherUser;

    public ChatControllerTests()
    {
        _owner = new User { Name = "Owner", Email = "owner@example.com", PasswordHash = "x" };
        _otherUser = new User { Name = "Other", Email = "other@example.com", PasswordHash = "x" };
        _db.Users.AddRange(_owner, _otherUser);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private ChatController CreateController(int callerId, params string[] roles)
    {
        var controller = new ChatController(
            _db,
            _pythonClient.Object,
            new CreateSessionRequestValidator(),
            new SendMessageRequestValidator(),
            NullLogger<ChatController>.Instance);
        controller.SetUser(ClaimsPrincipalFactory.Create(callerId, email: null, roles));
        return controller;
    }

    private ChatSession SeedSession(User owner, string title = "Existing chat")
    {
        var session = new ChatSession { Id = Guid.NewGuid(), UserId = owner.Id, Title = title };
        _db.ChatSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    [Fact]
    public async Task CreateSession_DefaultsTitle_WhenNoneProvided()
    {
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.CreateSession(new CreateSessionRequest { Title = null }, CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<SessionResponse>(created.Value);
        Assert.Equal("New chat", body.Title);
        Assert.Single(_db.ChatSessions);
    }

    [Fact]
    public async Task ListSessions_OnlyReturnsCallersOwnSessions()
    {
        SeedSession(_owner, "Mine");
        SeedSession(_otherUser, "Not mine");
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.ListSessions(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sessions = Assert.IsAssignableFrom<IReadOnlyList<SessionResponse>>(ok.Value);
        Assert.Single(sessions);
        Assert.Equal("Mine", sessions[0].Title);
    }

    [Fact]
    public async Task SendMessage_ReturnsOk_WithReplyFromPythonClient_AndPersistsUsageLog()
    {
        var session = SeedSession(_owner);
        _pythonClient
            .Setup(c => c.GenerateAsync(session.Id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "Hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("Hi there!", 42));
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.SendMessage(session.Id, new SendMessageRequest { Content = "Hello" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SendMessageResponse>(ok.Value);
        Assert.Equal("Hello", body.UserMessage.Content);
        Assert.Equal("user", body.UserMessage.Role);
        Assert.Equal("Hi there!", body.AssistantMessage.Content);
        Assert.Equal("assistant", body.AssistantMessage.Role);

        Assert.Equal(2, _db.Messages.Count(m => m.SessionId == session.Id));
        var usageLog = Assert.Single(_db.UsageLogs);
        Assert.Equal(42, usageLog.TokensUsed);
        Assert.Equal(_owner.Id, usageLog.UserId);
    }

    [Fact]
    public async Task SendMessage_Returns503_AndStillPersistsUserMessage_WhenPythonIsUnavailable()
    {
        var session = SeedSession(_owner);
        _pythonClient
            .Setup(c => c.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PythonServiceUnavailableException("down"));
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.SendMessage(session.Id, new SendMessageRequest { Content = "Hello" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        // The core CLAUDE.md guarantee: a Python failure never loses the user's message.
        var persisted = Assert.Single(_db.Messages);
        Assert.Equal("Hello", persisted.Content);
        Assert.Equal(MessageRole.User, persisted.Role);
        Assert.Empty(_db.UsageLogs);
    }

    [Fact]
    public async Task SendMessage_Returns400_ForEmptyContent()
    {
        var session = SeedSession(_owner);
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.SendMessage(session.Id, new SendMessageRequest { Content = "   " }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        _pythonClient.Verify(
            c => c.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessage_Returns404_WhenSessionBelongsToAnotherUser()
    {
        var session = SeedSession(_otherUser);
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.SendMessage(session.Id, new SendMessageRequest { Content = "Hello" }, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task GetMessages_Returns404_WhenSessionDoesNotExist()
    {
        var controller = CreateController(_owner.Id, "User");

        var result = await controller.GetMessages(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMessages_AllowsAdmin_ToReadAnotherUsersSession()
    {
        var session = SeedSession(_owner);
        _db.Messages.Add(new Message { SessionId = session.Id, Role = MessageRole.User, Content = "Hi" });
        _db.SaveChanges();
        var adminController = CreateController(_otherUser.Id, "Admin");

        var result = await adminController.GetMessages(session.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var messages = Assert.IsAssignableFrom<IReadOnlyList<MessageResponse>>(ok.Value);
        Assert.Single(messages);
    }
}
