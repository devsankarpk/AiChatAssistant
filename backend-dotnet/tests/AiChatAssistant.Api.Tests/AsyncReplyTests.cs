using System.Text.Json;
using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Async AI replies, request side (Phase 18): <c>Chat:AsyncReplies</c> on makes the send request answer 202 and queue a job.</summary>
public class AsyncReplyControllerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IPythonAiClient> _python = new();
    private readonly Mock<IAiReplyQueue> _queue = new();
    private readonly User _owner;
    private readonly User _other;

    public AsyncReplyControllerTests()
    {
        _owner = new User { Name = "Owner", Email = "owner@example.com", PasswordHash = "x" };
        _other = new User { Name = "Other", Email = "other@example.com", PasswordHash = "x" };
        _db.Users.AddRange(_owner, _other);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private ChatController Create(User caller, bool asyncReplies = true, IAiReplyQueue? queue = null, params string[] roles)
    {
        var controller = new ChatController(
            _db, _python.Object, new CreateSessionRequestValidator(), new SendMessageRequestValidator(),
            NullLogger<ChatController>.Instance, Options.Create(new ChatOptions { AsyncReplies = asyncReplies }),
            queue ?? _queue.Object);
        controller.SetUser(ClaimsPrincipalFactory.Create(caller.Id, caller.Email, roles));
        return controller;
    }

    private ChatSession Seed(User owner, string title = "Existing")
    {
        var session = new ChatSession { Id = Guid.NewGuid(), UserId = owner.Id, Title = title };
        _db.ChatSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    private AiReplyJob SeedJob(ChatSession session, User owner, AiReplyJobStatus status)
    {
        var message = new Message { SessionId = session.Id, Role = MessageRole.User, Content = "question" };
        _db.Messages.Add(message);
        _db.SaveChanges();
        var job = new AiReplyJob { Id = Guid.NewGuid(), SessionId = session.Id, UserId = owner.Id, UserMessageId = message.Id, Status = status, ClaimToken = Guid.NewGuid() };
        _db.AiReplyJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    [Fact]
    public async Task AsyncOn_Answers202_SavesTheQuestionAndAJob_QueuesTheJobId_AndNeverCallsPython()
    {
        var session = Seed(_owner);

        var result = await Create(_owner).SendMessage(session.Id, new SendMessageRequest { Content = "  hello there  " }, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var body = Assert.IsType<SendMessageAcceptedResponse>(accepted.Value);
        Assert.Equal("pending", body.ReplyStatus);
        Assert.Equal("hello there", body.UserMessage.Content);
        var job = Assert.Single(_db.AiReplyJobs);
        Assert.Equal(body.JobId, job.Id);
        Assert.Equal(AiReplyJobStatus.Pending, job.Status);
        Assert.Equal(body.UserMessage.Id, job.UserMessageId);
        Assert.Equal(_owner.Id, job.UserId);
        _queue.Verify(q => q.EnqueueAsync(job.Id, It.IsAny<CancellationToken>()), Times.Once);
        _python.VerifyNoOtherCalls();
        Assert.Single(_db.Messages); // only the question; the reply comes later
    }

    [Fact]
    public async Task AsyncOn_StillTitlesAnUntitledChat_BeforeTheReply()
    {
        var session = Seed(_owner, "New chat");

        await Create(_owner).SendMessage(session.Id, new SendMessageRequest { Content = "Plan a week of dinners" }, CancellationToken.None);

        Assert.Equal("Plan a week of dinners", _db.ChatSessions.Single(s => s.Id == session.Id).Title);
    }

    [Fact]
    public async Task AsyncOn_WhenTheBrokerRefusesTheJob_Answers503_KeepsTheQuestion_AndRemovesTheOrphanJob()
    {
        var session = Seed(_owner);
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("broker down"));

        var result = await Create(_owner).SendMessage(session.Id, new SendMessageRequest { Content = "hi" }, CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("ai_service_unavailable", Assert.IsType<ErrorResponse>(response.Value).Error.Code);
        Assert.Single(_db.Messages);          // the user message stays persisted (CLAUDE.md)
        Assert.Empty(_db.AiReplyJobs);        // nothing waits for a reply that will never come
    }

    [Fact]
    public async Task AsyncOff_StaysSynchronous_EvenWhenAQueueIsAvailable()
    {
        var session = Seed(_owner);
        _python.Setup(p => p.GenerateAsync(session.Id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("hello", 7));

        var result = await Create(_owner, asyncReplies: false).SendMessage(session.Id, new SendMessageRequest { Content = "hi" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("hello", Assert.IsType<SendMessageResponse>(ok.Value).AssistantMessage.Content);
        Assert.Empty(_db.AiReplyJobs);
        _queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AsyncOn_ForSomeoneElsesChat_Is404_AndQueuesNothing()
    {
        var session = Seed(_owner);

        var result = await Create(_other).SendMessage(session.Id, new SendMessageRequest { Content = "hi" }, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        _queue.VerifyNoOtherCalls();
        Assert.Empty(_db.AiReplyJobs);
    }

    [Fact]
    public async Task OpenJobs_ListsPendingAndFailed_NotDone_AndOnlyForTheOwner()
    {
        var session = Seed(_owner);
        var pending = SeedJob(session, _owner, AiReplyJobStatus.Pending);
        var processing = SeedJob(session, _owner, AiReplyJobStatus.Processing);
        var failed = SeedJob(session, _owner, AiReplyJobStatus.Failed);
        SeedJob(session, _owner, AiReplyJobStatus.Done);

        var result = await Create(_owner).GetOpenReplyJobs(session.Id, CancellationToken.None);

        var jobs = Assert.IsAssignableFrom<IReadOnlyList<ReplyJobResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, jobs.Count);
        Assert.Equal("pending", jobs.Single(j => j.JobId == pending.Id).Status);
        Assert.Equal("pending", jobs.Single(j => j.JobId == processing.Id).Status); // "processing" is an internal detail
        Assert.Equal("failed", jobs.Single(j => j.JobId == failed.Id).Status);
        Assert.NotNull(jobs.Single(j => j.JobId == failed.Id).Error);
        Assert.IsType<NotFoundObjectResult>((await Create(_other).GetOpenReplyJobs(session.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Retry_PutsAFailedJobBackInTheQueue_WithoutANewQuestion()
    {
        var session = Seed(_owner);
        var job = SeedJob(session, _owner, AiReplyJobStatus.Failed);
        job.Attempts = 3; job.Error = "boom"; _db.SaveChanges();

        var result = await Create(_owner).RetryReply(job.Id, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        Assert.Equal("pending", Assert.IsType<RetryReplyResponse>(accepted.Value).ReplyStatus);
        var reloaded = _db.AiReplyJobs.Single();
        Assert.Equal(AiReplyJobStatus.Pending, reloaded.Status);
        Assert.Equal(0, reloaded.Attempts);
        Assert.Null(reloaded.Error);
        _queue.Verify(q => q.EnqueueAsync(job.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_db.Messages); // still just the original question
    }

    [Fact]
    public async Task Retry_OfAJobThatIsAlreadyPending_IsIdempotent_AndDoesNotQueueTwice()
    {
        var session = Seed(_owner);
        var job = SeedJob(session, _owner, AiReplyJobStatus.Pending);

        var result = await Create(_owner).RetryReply(job.Id, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result.Result);
        _queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Retry_OfADeliveredReply_Is409()
    {
        var job = SeedJob(Seed(_owner), _owner, AiReplyJobStatus.Done);

        var result = await Create(_owner).RetryReply(job.Id, CancellationToken.None);

        Assert.Equal("reply_already_delivered", Assert.IsType<ErrorResponse>(Assert.IsType<ConflictObjectResult>(result.Result).Value).Error.Code);
    }

    [Fact]
    public async Task Retry_OfSomeoneElsesJob_Is404_ButAnAdminMayRetryAnyJob()
    {
        var job = SeedJob(Seed(_owner), _owner, AiReplyJobStatus.Failed);

        Assert.IsType<NotFoundObjectResult>((await Create(_other).RetryReply(job.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundObjectResult>((await Create(_owner).RetryReply(Guid.NewGuid(), CancellationToken.None)).Result);
        Assert.IsType<AcceptedResult>((await Create(_other, roles: "Admin").RetryReply(job.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Retry_WhenTheBrokerRefuses_Is503_AndTheJobStaysFailed()
    {
        var job = SeedJob(Seed(_owner), _owner, AiReplyJobStatus.Failed);
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("down"));

        var result = await Create(_owner).RetryReply(job.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(AiReplyJobStatus.Failed, _db.AiReplyJobs.Single().Status);
    }
}

/// <summary>The worker's decisions (Phase 18): idempotency, claims and leases, retries and the final failure. No broker involved.</summary>
public class AiReplyProcessorTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dbName = Guid.NewGuid().ToString();
    // One root shared by every context in a test, so contexts with different options (an interceptor) still see the same data.
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly AppDbContext _db;
    private readonly Mock<IPythonAiClient> _python = new();
    private readonly Mock<IChatNotifier> _notifier = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly User _user;
    private readonly ChatSession _session;

    public AiReplyProcessorTests()
    {
        _db = NewContext();
        _db.Database.EnsureCreated();
        _user = new User { Name = "U", Email = "u@example.com", PasswordHash = "x" };
        _db.Users.Add(_user);
        _db.SaveChanges();
        _session = new ChatSession { Id = Guid.NewGuid(), UserId = _user.Id, Title = "t" };
        _db.ChatSessions.Add(_session);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AppDbContext NewContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName, _root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning));
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new AppDbContext(builder.Options);
    }

    private AiReplyProcessor Processor(AppDbContext? db = null, IPythonAiClient? python = null, int[]? delays = null, int leaseSeconds = 90) =>
        new(db ?? _db, python ?? _python.Object, _notifier.Object,
            Options.Create(new ChatOptions { ReplyRetryDelaysSeconds = delays ?? new[] { 5, 20 }, ReplyLeaseSeconds = leaseSeconds }),
            _clock, NullLogger<AiReplyProcessor>.Instance);

    private AiReplyJob NewJob(string question = "What is DNS?", AiReplyJobStatus status = AiReplyJobStatus.Pending)
    {
        var message = new Message { SessionId = _session.Id, Role = MessageRole.User, Content = question };
        _db.Messages.Add(message);
        _db.SaveChanges();
        var job = new AiReplyJob { Id = Guid.NewGuid(), SessionId = _session.Id, UserId = _user.Id, UserMessageId = message.Id, Status = status, ClaimToken = Guid.NewGuid() };
        _db.AiReplyJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private void PythonReplies(string reply = "It maps names to addresses.", int tokens = 42) =>
        _python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult(reply, tokens));

    private void PythonFails() =>
        _python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PythonServiceUnavailableException("AI service returned 502"));

    [Fact]
    public async Task Success_SavesTheReplyAndUsage_MarksTheJobDone_AndPushesAssistantReplied()
    {
        var job = NewJob("What is DNS?");
        PythonReplies("It maps names to addresses.", 42);

        var result = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Done, result.Outcome);
        var saved = _db.AiReplyJobs.Include(j => j.AssistantMessage).Single();
        Assert.Equal(AiReplyJobStatus.Done, saved.Status);
        Assert.Equal(1, saved.Attempts);
        Assert.Equal("It maps names to addresses.", saved.AssistantMessage!.Content);
        Assert.Equal(MessageRole.Assistant, saved.AssistantMessage.Role);
        var usage = Assert.Single(_db.UsageLogs);
        Assert.Equal((_user.Id, _session.Id, 42), (usage.UserId, usage.SessionId, usage.TokensUsed));
        _notifier.Verify(n => n.AssistantRepliedAsync(_user.Id, job.Id, _session.Id, job.UserMessageId,
            It.Is<MessageResponse>(m => m.Content == "It maps names to addresses." && m.Role == "assistant"), It.IsAny<CancellationToken>()), Times.Once);
        _python.Verify(p => p.GenerateAsync(_session.Id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "What is DNS?", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ADuplicateDelivery_OfADoneJob_DoesNothing_NoSecondCallNoSecondReply()
    {
        var job = NewJob();
        PythonReplies();
        await Processor().ProcessAsync(job.Id, CancellationToken.None);

        var again = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.AlreadyHandled, again.Outcome);
        _python.Verify(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_db.Messages.Where(m => m.Role == MessageRole.Assistant));
        Assert.Single(_db.UsageLogs);
        _notifier.Verify(n => n.AssistantRepliedAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<MessageResponse>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AJobThatDoesNotExist_IsDropped()
    {
        var result = await Processor().ProcessAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Missing, result.Outcome);
        _python.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AFailedAiCall_WithRetriesLeft_ParksTheJobForTheConfiguredDelay_AndTellsNoOne()
    {
        var job = NewJob();
        PythonFails();

        var first = await Processor().ProcessAsync(job.Id, CancellationToken.None);
        var second = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal((AiReplyOutcome.Retry, TimeSpan.FromSeconds(5)), (first.Outcome, first.Delay));
        Assert.Equal((AiReplyOutcome.Retry, TimeSpan.FromSeconds(20)), (second.Outcome, second.Delay));
        var saved = _db.AiReplyJobs.Single();
        Assert.Equal(AiReplyJobStatus.Pending, saved.Status);
        Assert.Equal(2, saved.Attempts);
        Assert.Equal("AI service returned 502", saved.Error);
        Assert.Null(saved.ClaimedAt);
        _notifier.Verify(n => n.AssistantFailedAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AfterTheLastRetry_TheJobFails_TheUserIsToldOnce_AndLaterDeliveriesAreIgnored()
    {
        var job = NewJob();
        PythonFails();
        var p = Processor();
        await p.ProcessAsync(job.Id, CancellationToken.None);
        await p.ProcessAsync(job.Id, CancellationToken.None);

        var last = await p.ProcessAsync(job.Id, CancellationToken.None);
        var after = await p.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Failed, last.Outcome);
        Assert.Equal(AiReplyOutcome.AlreadyHandled, after.Outcome);
        Assert.Equal(AiReplyJobStatus.Failed, _db.AiReplyJobs.Single().Status);
        Assert.Equal(3, _db.AiReplyJobs.Single().Attempts); // three attempts in all: the first try plus two retries
        _notifier.Verify(n => n.AssistantFailedAsync(_user.Id, job.Id, _session.Id, job.UserMessageId, AiReplyProcessor.FailureCode, AiReplyProcessor.FailureMessage, It.IsAny<CancellationToken>()), Times.Once);
        _python.Verify(x => x.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task WithNoRetriesConfigured_TheFirstFailureIsFinal()
    {
        var job = NewJob();
        PythonFails();

        var result = await Processor(delays: Array.Empty<int>()).ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AJobHeldByALiveWorker_IsLeftAlone_AndLookedAtAgainAfterTheLease()
    {
        var job = NewJob();
        job.Status = AiReplyJobStatus.Processing; job.ClaimedAt = Start.UtcDateTime; _db.SaveChanges();
        _clock.Advance(TimeSpan.FromSeconds(30)); // well inside the 90 s lease

        var result = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal((AiReplyOutcome.Deferred, TimeSpan.FromSeconds(90)), (result.Outcome, result.Delay));
        _python.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AJobAbandonedByADeadWorker_IsTakenOverOnceTheLeaseHasPassed()
    {
        var job = NewJob();
        job.Status = AiReplyJobStatus.Processing; job.ClaimedAt = Start.UtcDateTime; job.Attempts = 1; _db.SaveChanges();
        _clock.Advance(TimeSpan.FromSeconds(91));
        PythonReplies();

        var result = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Done, result.Outcome);
        Assert.Equal(2, _db.AiReplyJobs.Single().Attempts);
    }

    [Fact]
    public async Task TwoWorkersRacingForTheSameJob_OnlyOneWins_TheOtherStandsDown()
    {
        var job = NewJob();
        PythonReplies();
        // The other worker claims the job (changing its claim token) at the moment this worker saves its own claim.
        var interceptor = new RivalClaimInterceptor(_dbName, _root, job.Id);
        var racing = NewContext(interceptor);

        var result = await Processor(racing).ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Deferred, result.Outcome);
        _python.VerifyNoOtherCalls();           // the loser never called the AI
        Assert.Empty(_db.UsageLogs);
    }

    [Fact]
    public async Task IfTheClaimIsLostDuringTheAiCall_TheLateReplyIsDiscarded_AndTheNewOwnersReplyStands()
    {
        var job = NewJob();
        var firstCall = true;
        _python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    // While the first worker waits for the AI its lease runs out, and a second worker finishes the job.
                    _clock.Advance(TimeSpan.FromSeconds(91));
                    using var other = NewContext();
                    await Processor(other).ProcessAsync(job.Id, CancellationToken.None);
                    return new PythonGenerateResult("late reply from the first worker", 1);
                }
                return new PythonGenerateResult("the second worker's reply", 2);
            });

        var result = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.AlreadyHandled, result.Outcome);
        // The job points at the second worker's reply, and the user was told once (by the worker that finished it).
        // (The in-memory test store does not roll a failed save back, so the stray rows are checked against real MySQL in the Docker run.)
        using var fresh = NewContext(); // read through a new context: the first worker's tracked copy still holds its discarded changes
        var finished = fresh.AiReplyJobs.Include(j => j.AssistantMessage).Single();
        Assert.Equal(AiReplyJobStatus.Done, finished.Status);
        Assert.Equal("the second worker's reply", finished.AssistantMessage!.Content);
        _notifier.Verify(n => n.AssistantRepliedAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<MessageResponse>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheQuestionIsReadFromTheDatabase_AndOnlyTheLast20EarlierMessagesAreSentAsContext_OldestFirst()
    {
        for (var i = 1; i <= 25; i++)
        {
            _db.Messages.Add(new Message { SessionId = _session.Id, Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant, Content = $"old {i}" });
        }
        _db.SaveChanges();
        var job = NewJob("the question");
        _db.Messages.Add(new Message { SessionId = _session.Id, Role = MessageRole.User, Content = "asked later" }); // after the question: must not be sent
        _db.SaveChanges();
        IReadOnlyList<PythonHistoryMessage>? sent = null;
        _python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<PythonHistoryMessage>, string, CancellationToken>((_, h, _, _) => sent = h)
            .ReturnsAsync(new PythonGenerateResult("ok", 1));

        await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(20, sent!.Count);
        Assert.Equal("old 6", sent[0].Content);
        Assert.Equal("old 25", sent[^1].Content);
        Assert.DoesNotContain(sent, m => m.Content is "the question" or "asked later");
        _python.Verify(p => p.GenerateAsync(_session.Id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "the question", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IfTheQuestionWasDeleted_TheJobFailsAtOnce_WithoutCallingTheAi()
    {
        var job = NewJob();
        _db.Messages.RemoveRange(_db.Messages.Where(m => m.Id == job.UserMessageId));
        _db.SaveChanges(); // cascade removes the job too in a real database; the in-memory store keeps it
        if (!_db.AiReplyJobs.Any()) { return; }

        var result = await Processor().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(AiReplyOutcome.Failed, result.Outcome);
        _python.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnUnexpectedError_IsNotSwallowed_SoTheWorkerCanParkTheMessage()
    {
        var job = NewJob();
        _python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(job.Id, CancellationToken.None));

        Assert.Equal(AiReplyJobStatus.Processing, _db.AiReplyJobs.Single().Status); // the lease protects it until it is picked up again
    }

    /// <summary>Another worker claims the job, from a different context, in the middle of this context's first save.</summary>
    private sealed class RivalClaimInterceptor : SaveChangesInterceptor
    {
        private readonly string _dbName;
        private readonly InMemoryDatabaseRoot _root;
        private readonly Guid _jobId;
        private bool _done;

        public RivalClaimInterceptor(string dbName, InMemoryDatabaseRoot root, Guid jobId)
        {
            _dbName = dbName;
            _root = root;
            _jobId = jobId;
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            RivalClaim();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RivalClaim();
            return ValueTask.FromResult(result);
        }

        private void RivalClaim()
        {
            if (_done) { return; }
            _done = true;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName, _root).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
            using var rival = new AppDbContext(options);
            var job = rival.AiReplyJobs.Single(j => j.Id == _jobId);
            job.Status = AiReplyJobStatus.Processing;
            job.ClaimedAt = DateTime.UtcNow;
            job.ClaimToken = Guid.NewGuid();
            rival.SaveChanges();
        }
    }
}

/// <summary>The push side, the queue message and the wiring of async replies (Phase 18).</summary>
public class AsyncReplyInfrastructureTests
{
    private static ServiceCollection Services(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmailDelivery(configuration);
        services.AddBrokerFeatures(configuration);
        return services;
    }

    private static readonly (string, string?)[] Broker = { ("RabbitMq:Host", "rabbitmq"), ("RabbitMq:Username", "u"), ("RabbitMq:Password", "p") };

    [Fact]
    public void AsyncRepliesOn_WithoutABroker_FailsAtStartup_WithAClearMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Services(("Chat:AsyncReplies", "true")));

        Assert.Contains("RabbitMq:Host", ex.Message);
    }

    [Fact]
    public async Task AsyncRepliesOn_WithABroker_RegistersTheQueueTheProcessorAndTheWorker()
    {
        var services = Services(Broker.Append(("Chat:AsyncReplies", "true")).ToArray());
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<RabbitMqAiReplyQueue>(provider.GetRequiredService<IAiReplyQueue>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is AiReplyWorker);
        Assert.Contains(services, d => d.ServiceType == typeof(AiReplyProcessor));
        Assert.Contains(services, d => d.ServiceType == typeof(IChatNotifier) && d.ImplementationType == typeof(SignalRChatNotifier));
    }

    [Fact]
    public async Task AsyncRepliesOff_RegistersNoQueueAndNoWorker_EvenWithABroker()
    {
        await using var provider = Services(Broker).BuildServiceProvider();

        Assert.Null(provider.GetService<IAiReplyQueue>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is AiReplyWorker);
    }

    [Fact]
    public void ReplyQueueMessage_CarriesTheJobIdAndNothingElse()
    {
        var id = Guid.NewGuid();

        var json = JsonSerializer.Serialize(new AiReplyMessage(id));

        Assert.Equal($"{{\"JobId\":\"{id}\"}}", json); // no question, no answer, no user
    }

    [Fact]
    public void RetryQueues_AreNamedAfterTheirDelay_AndIncludeTheLeaseForDeferredJobs()
    {
        var options = new ChatOptions { ReplyRetryDelaysSeconds = new[] { 5, 20 }, ReplyLeaseSeconds = 90 };

        Assert.Equal("ai.retry.5s", AiReplyTopology.RetryQueue(5));
        Assert.Equal(new[] { 5, 20, 90 }, AiReplyTopology.RetryDelays(options));
        Assert.Equal(new[] { 5, 20 }, AiReplyTopology.RetryDelays(new ChatOptions { ReplyRetryDelaysSeconds = new[] { 5, 20 }, ReplyLeaseSeconds = 20 }));
    }

    [Fact]
    public async Task TheNotifier_SendsAssistantReplied_ToTheUsersGroup_WithTheJobAndTheReply()
    {
        var proxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<AiChatAssistant.Api.Hubs.DirectHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var notifier = new SignalRChatNotifier(hub.Object, NullLogger<SignalRChatNotifier>.Instance);
        var jobId = Guid.NewGuid();

        await notifier.AssistantRepliedAsync(9, jobId, Guid.NewGuid(), 4, new MessageResponse { Id = 11, Role = "assistant", Content = "hi" });

        clients.Verify(c => c.Group("user-9"), Times.Once);
        proxy.Verify(p => p.SendCoreAsync("AssistantReplied", It.Is<object?[]>(a => a.Length == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheNotifier_SendsAssistantFailed_AndNeverThrowsWhenThePushFails()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("hub down"));
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<AiChatAssistant.Api.Hubs.DirectHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var notifier = new SignalRChatNotifier(hub.Object, NullLogger<SignalRChatNotifier>.Instance);

        var ex = await Record.ExceptionAsync(() => notifier.AssistantFailedAsync(9, Guid.NewGuid(), Guid.NewGuid(), 4, "ai_service_unavailable", "x"));

        Assert.Null(ex);
        proxy.Verify(p => p.SendCoreAsync("AssistantFailed", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheSharedHistoryHelper_ReturnsOldestFirst_AndExcludesTheQuestionItself()
    {
        using var db = TestDbContextFactory.Create();
        var user = new User { Name = "U", Email = "u@example.com", PasswordHash = "x" };
        db.Users.Add(user); db.SaveChanges();
        var session = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "t" };
        db.ChatSessions.Add(session); db.SaveChanges();
        db.Messages.AddRange(
            new Message { SessionId = session.Id, Role = MessageRole.User, Content = "a" },
            new Message { SessionId = session.Id, Role = MessageRole.Assistant, Content = "b" },
            new Message { SessionId = session.Id, Role = MessageRole.User, Content = "q" });
        db.SaveChanges();
        var q = db.Messages.Single(m => m.Content == "q");

        var history = await ChatHistory.LoadAsync(db, session.Id, q.Id, CancellationToken.None);

        Assert.Equal(new[] { ("user", "a"), ("assistant", "b") }, history.Select(h => (h.Role, h.Content)).ToArray());
    }
}
