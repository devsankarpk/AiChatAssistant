using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// The real <c>Program.cs</c> pipeline in memory (see <see cref="ApiFactory"/>): the error shape on the paths that never reach a
/// controller (401, 403, 404), unhandled exceptions, the SignalR hub's token rules, and the HTTP contract of the chat endpoints.
/// </summary>
public class PipelineTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PipelineTests(ApiFactory factory)
    {
        _factory = factory;
        // The factory is shared by the tests of this class, but each test sets up its own fakes.
        _factory.Python.Reset();
        _factory.Email.Reset();
    }

    private static async Task<(string Code, string Message)> ErrorOf(HttpResponseMessage response)
    {
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType + "");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    private Guid SeedSession(int userId)
    {
        var id = Guid.NewGuid();
        _factory.WithDb(db =>
        {
            db.ChatSessions.Add(new ChatSession { Id = id, UserId = userId, Title = "New chat" });
            db.SaveChanges();
            return 0;
        });
        return id;
    }

    // ---- ERR-01..05, ADM-05 ----

    [Fact]
    public async Task ProtectedRoute_WithoutAToken_Is401_InTheStandardShape()
    {
        var response = await _factory.CreateClient().GetAsync("/api/chat/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await ErrorOf(response)).Code);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.invalidsignature")]
    public async Task ProtectedRoute_WithAGarbageOrMisSignedToken_Is401_InTheStandardShape(string token)
    {
        var response = await _factory.CreateClient().WithToken(token).GetAsync("/api/chat/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await ErrorOf(response)).Code);
    }

    [Fact]
    public async Task ProtectedRoute_WithATokenSignedByAnotherKey_Is401()
    {
        var other = new TokenService(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "a-completely-different-signing-key-0123456789" }).Build());
        var token = other.CreateToken(new User { Id = 1, Email = "x@example.com" }, new[] { "User" }).Token;

        var response = await _factory.CreateClient().WithToken(token).GetAsync("/api/chat/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoute_AsANormalUser_Is403_InTheStandardShape()
    {
        var (_, token) = _factory.CreateUser("Normal", "normal.403@example.com", "User");

        var response = await _factory.CreateClient().WithToken(token).GetAsync("/api/admin/usage");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", (await ErrorOf(response)).Code);
    }

    [Fact]
    public async Task AdminRoute_AsAnAdmin_Is200()
    {
        var (_, token) = _factory.CreateUser("Boss", "boss.200@example.com", "Admin");

        var response = await _factory.CreateClient().WithToken(token).GetAsync("/api/admin/usage?pageSize=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(100, doc.RootElement.GetProperty("pageSize").GetInt32()); // capped
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/nope")]
    public async Task UnknownRoute_Is404_InTheStandardShape(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ErrorOf(response)).Code);
    }

    [Fact]
    public async Task UnhandledException_Is500_InTheStandardShape_WithNoStackTraceOrExceptionText()
    {
        var (user, token) = _factory.CreateUser("Boom", "boom.500@example.com", "User");
        var session = SeedSession(user.Id);
        _factory.Python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret connection string Server=prod;Password=hunter2"));

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "hi" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("internal_error", body);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain(" at ", body); // no stack frames
    }

    [Fact]
    public async Task Health_Is200_WithoutAToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MalformedJsonBody_Is400_InTheStandardShape()
    {
        var response = await _factory.CreateClient().PostAsync("/api/auth/login", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await ErrorOf(response)).Code);
    }

    // ---- the chat HTTP contract ----

    [Fact]
    public async Task SendMessage_WhenPythonIsUnavailable_Is503_AndTheQuestionStaysSaved()
    {
        var (user, token) = _factory.CreateUser("Asker", "asker.503@example.com", "User");
        var session = SeedSession(user.Id);
        _factory.Python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PythonServiceUnavailableException("down"));

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "are you there" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ai_service_unavailable", (await ErrorOf(response)).Code);
        Assert.Equal(1, _factory.WithDb(db => db.Messages.Count(m => m.SessionId == session)));
    }

    [Fact]
    public async Task SendMessage_WhenPythonAnswers_Is200_WithBothMessages()
    {
        var (user, token) = _factory.CreateUser("Happy", "happy.200@example.com", "User");
        var session = SeedSession(user.Id);
        _factory.Python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("hi there", 7));

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "hello" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("hello", doc.RootElement.GetProperty("userMessage").GetProperty("content").GetString());
        Assert.Equal("hi there", doc.RootElement.GetProperty("assistantMessage").GetProperty("content").GetString());
        Assert.False(doc.RootElement.TryGetProperty("replyStatus", out _));
    }

    [Fact]
    public async Task SendMessage_ToSomeoneElsesSession_Is404_NotForbidden()
    {
        var (owner, _) = _factory.CreateUser("Owner", "owner.404@example.com", "User");
        var (_, strangerToken) = _factory.CreateUser("Stranger", "stranger.404@example.com", "User");
        var session = SeedSession(owner.Id);

        var response = await _factory.CreateClient().WithToken(strangerToken).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "hi" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", (await ErrorOf(response)).Code);
    }

    // ---- forgot-password through the pipeline ----

    [Fact]
    public async Task ForgotPassword_GivesTheSameAnswer_ForUnknownKnownAndBrokenQueue()
    {
        _factory.CreateUser("Known", "known.reset@example.com", "User");
        _factory.CreateUser("Broken", "broken.reset@example.com", "User");
        _factory.Email.Setup(e => e.EnqueuePasswordResetAsync("broken.reset@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokerUnavailableException("broker is silent"));
        var client = _factory.CreateClient();

        var bodies = new List<string>();
        foreach (var email in new[] { "nobody.reset@example.com", "known.reset@example.com", "broken.reset@example.com" })
        {
            var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
    }

    // ---- the SignalR hub's token rules (negotiate is the first request every client makes) ----

    [Fact]
    public async Task Hub_RejectsAnAnonymousClient_With401()
    {
        var response = await _factory.CreateClient().PostAsync("/hubs/direct/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_AcceptsATokenInTheAccessTokenQueryString()
    {
        var (_, token) = _factory.CreateUser("Socket", "socket.hub@example.com", "User");

        var response = await _factory.CreateClient().PostAsync($"/hubs/direct/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Hub_RejectsAGarbageAccessToken()
    {
        var response = await _factory.CreateClient().PostAsync("/hubs/direct/negotiate?negotiateVersion=1&access_token=garbage", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessTokenInTheQueryString_IsIgnoredOutsideTheHubPath()
    {
        var (_, token) = _factory.CreateUser("Query", "query.token@example.com", "User");

        var response = await _factory.CreateClient().GetAsync($"/api/chat/sessions?access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- direct chat privacy through the pipeline ----

    [Fact]
    public async Task DirectConversation_IsInvisibleToAnAdminWhoIsNotAParticipant()
    {
        var (alice, aliceToken) = _factory.CreateUser("Alice", "alice.direct@example.com", "User");
        var (bob, _) = _factory.CreateUser("Bob", "bob.direct@example.com", "User");
        var (_, adminToken) = _factory.CreateUser("Root", "root.direct@example.com", "Admin");

        var started = await _factory.CreateClient().WithToken(aliceToken).PostAsJsonAsync("/api/direct/conversations", new { userId = bob.Id });
        Assert.True(started.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var conversationId = doc.RootElement.GetProperty("id").GetGuid();

        var admin = _factory.CreateClient().WithToken(adminToken);
        var read = await admin.GetAsync($"/api/direct/conversations/{conversationId}/messages");
        var send = await admin.PostAsJsonAsync($"/api/direct/conversations/{conversationId}/messages", new { content = "snoop" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("conversation_not_found", (await ErrorOf(read)).Code);
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        _ = alice;
    }

    [Fact]
    public async Task DirectUserSearch_NeverReturnsEmails()
    {
        var (_, token) = _factory.CreateUser("Searcher", "searcher.direct@example.com", "User");
        _factory.CreateUser("Zelda Findable", "zelda.secret@example.com", "User");

        var response = await _factory.CreateClient().WithToken(token).GetAsync("/api/direct/users?search=zel");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Zelda Findable", body);
        Assert.DoesNotContain("zelda.secret", body);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The async-reply HTTP contract (Phase 18), with the broker replaced by a mock and no workers running.</summary>
public class AsyncReplyPipelineTests : IClassFixture<AsyncReplyPipelineTests.AsyncFactory>
{
    public class AsyncFactory : ApiFactory
    {
        public AsyncFactory() { AsyncReplies = true; }
    }

    private readonly ApiFactory _factory;

    public AsyncReplyPipelineTests(AsyncFactory factory)
    {
        _factory = factory;
        _factory.ReplyQueue.Reset();
        _factory.Python.Reset();
    }

    private Guid SeedSession(int userId)
    {
        var id = Guid.NewGuid();
        _factory.WithDb(db => { db.ChatSessions.Add(new ChatSession { Id = id, UserId = userId, Title = "New chat" }); db.SaveChanges(); return 0; });
        return id;
    }

    [Fact]
    public async Task Send_Is202_Pending_AndTheJobIsQueued_WithoutCallingPython()
    {
        var (user, token) = _factory.CreateUser("Async", "async.202@example.com", "User");
        var session = SeedSession(user.Id);

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "slow question" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pending", doc.RootElement.GetProperty("replyStatus").GetString());
        var jobId = doc.RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal("slow question", doc.RootElement.GetProperty("userMessage").GetProperty("content").GetString());
        _factory.ReplyQueue.Verify(q => q.EnqueueAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        _factory.Python.Verify(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_WhenTheBrokerRefusesTheJob_Is503_QuestionKept_NoOrphanJob()
    {
        var (user, token) = _factory.CreateUser("Async", "async.503@example.com", "User");
        var session = SeedSession(user.Id);
        _factory.ReplyQueue.Setup(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new BrokerUnavailableException("silent"));

        var response = await _factory.CreateClient().WithToken(token).PostAsJsonAsync($"/api/chat/sessions/{session}/messages", new { content = "q" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("ai_service_unavailable", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, _factory.WithDb(db => db.Messages.Count(m => m.SessionId == session)));
        Assert.Equal(0, _factory.WithDb(db => db.AiReplyJobs.Count(j => j.SessionId == session)));
    }

    private Guid SeedJob(int userId, Guid sessionId, AiReplyJobStatus status)
    {
        return _factory.WithDb(db =>
        {
            var message = new Message { SessionId = sessionId, Role = MessageRole.User, Content = "q" };
            db.Messages.Add(message);
            var job = new AiReplyJob { Id = Guid.NewGuid(), SessionId = sessionId, UserId = userId, UserMessage = message, Status = status, ClaimToken = Guid.NewGuid() };
            db.AiReplyJobs.Add(job);
            db.SaveChanges();
            return job.Id;
        });
    }

    [Fact]
    public async Task Jobs_ListsOnlyOpenOnes_AndHidesDoneOnes()
    {
        var (user, token) = _factory.CreateUser("Jobs", "jobs.list@example.com", "User");
        var session = SeedSession(user.Id);
        var pending = SeedJob(user.Id, session, AiReplyJobStatus.Pending);
        var failed = SeedJob(user.Id, session, AiReplyJobStatus.Failed);
        SeedJob(user.Id, session, AiReplyJobStatus.Done);

        var response = await _factory.CreateClient().WithToken(token).GetAsync($"/api/chat/sessions/{session}/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobs = doc.RootElement.EnumerateArray().ToDictionary(j => j.GetProperty("jobId").GetGuid(), j => j.GetProperty("status").GetString());
        Assert.Equal(2, jobs.Count);
        Assert.Equal("pending", jobs[pending]);
        Assert.Equal("failed", jobs[failed]);
    }

    [Fact]
    public async Task Jobs_OfSomeoneElsesSession_Is404()
    {
        var (owner, _) = _factory.CreateUser("Owner", "jobs.owner@example.com", "User");
        var (_, stranger) = _factory.CreateUser("Stranger", "jobs.stranger@example.com", "User");
        var session = SeedSession(owner.Id);

        var response = await _factory.CreateClient().WithToken(stranger).GetAsync($"/api/chat/sessions/{session}/jobs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Jobs_WithoutAToken_Is401()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/chat/sessions/{Guid.NewGuid()}/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Retry_OfAFailedJob_Is202_AndRequeuesIt()
    {
        var (user, token) = _factory.CreateUser("Retry", "retry.202@example.com", "User");
        var job = SeedJob(user.Id, SeedSession(user.Id), AiReplyJobStatus.Failed);

        var response = await _factory.CreateClient().WithToken(token).PostAsync($"/api/chat/jobs/{job}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _factory.ReplyQueue.Verify(q => q.EnqueueAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(AiReplyJobStatus.Pending, _factory.WithDb(db => db.AiReplyJobs.Single(j => j.Id == job).Status));
    }

    [Fact]
    public async Task Retry_OfADeliveredJob_Is409()
    {
        var (user, token) = _factory.CreateUser("Retry", "retry.409@example.com", "User");
        var job = SeedJob(user.Id, SeedSession(user.Id), AiReplyJobStatus.Done);

        var response = await _factory.CreateClient().WithToken(token).PostAsync($"/api/chat/jobs/{job}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("reply_already_delivered", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Retry_OfSomeoneElsesOrUnknownJob_Is404()
    {
        var (owner, _) = _factory.CreateUser("Owner", "retry.owner@example.com", "User");
        var (_, stranger) = _factory.CreateUser("Stranger", "retry.stranger@example.com", "User");
        var job = SeedJob(owner.Id, SeedSession(owner.Id), AiReplyJobStatus.Failed);
        var client = _factory.CreateClient().WithToken(stranger);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/chat/jobs/{job}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/chat/jobs/{Guid.NewGuid()}/retry", null)).StatusCode);
    }

    [Fact]
    public async Task Retry_WhenTheBrokerIsDown_Is503_AndTheJobStaysFailed()
    {
        var (user, token) = _factory.CreateUser("Retry", "retry.503@example.com", "User");
        var job = SeedJob(user.Id, SeedSession(user.Id), AiReplyJobStatus.Failed);
        _factory.ReplyQueue.Setup(q => q.EnqueueAsync(job, It.IsAny<CancellationToken>())).ThrowsAsync(new BrokerUnavailableException("silent"));

        var response = await _factory.CreateClient().WithToken(token).PostAsync($"/api/chat/jobs/{job}/retry", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(AiReplyJobStatus.Failed, _factory.WithDb(db => db.AiReplyJobs.Single(j => j.Id == job).Status));
    }
}
