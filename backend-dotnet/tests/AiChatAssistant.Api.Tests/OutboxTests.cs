using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Events are written with the change (Phase 19): ids and status only, never text, and only when the change itself is saved.</summary>
public class OutboxWritingTests : IDisposable
{
    private const string SecretText = "my private plan for saturday";
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly InMemoryPresenceTracker _presence = new();
    private readonly Mock<IDirectNotifier> _notifier = new();
    private readonly User _alice;
    private readonly User _bob;
    private readonly DirectConversation _conversation;

    public OutboxWritingTests()
    {
        _alice = new User { Name = "Alice", Email = "a@example.com", PasswordHash = "x" };
        _bob = new User { Name = "Bob", Email = "b@example.com", PasswordHash = "x" };
        _db.Users.AddRange(_alice, _bob);
        _db.SaveChanges();
        _conversation = new DirectConversation { Id = Guid.NewGuid(), UserLowId = Math.Min(_alice.Id, _bob.Id), UserHighId = Math.Max(_alice.Id, _bob.Id) };
        _db.DirectConversations.Add(_conversation);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DirectChatController Controller(User caller, IOutbox? outbox = null)
    {
        var controller = new DirectChatController(
            _db, new StartConversationRequestValidator(), new SendDirectMessageRequestValidator(),
            _presence, _notifier.Object, outbox ?? new OutboxWriter(_db));
        controller.SetUser(ClaimsPrincipalFactory.Create(caller.Id, caller.Email));
        return controller;
    }

    private List<(OutboxMessage Row, DirectEventPayload Payload)> Events() =>
        _db.OutboxMessages.OrderBy(o => o.Id).ToList()
            .Select(o => (o, JsonSerializer.Deserialize<DirectEventPayload>(o.Payload, DirectEvents.Json)!)).ToList();

    [Fact]
    public async Task SendingAMessage_WritesASentEvent_WithIdsAndNoText()
    {
        await Controller(_alice).SendMessage(_conversation.Id, new SendDirectMessageRequest { Content = SecretText }, CancellationToken.None);

        var (row, payload) = Assert.Single(Events());
        var message = _db.DirectMessages.Single();
        Assert.Equal("direct.message.sent", row.Type);
        Assert.Equal(new[] { message.Id }, payload.MessageIds);
        Assert.Equal((_conversation.Id, _alice.Id, _bob.Id, "sent"), (payload.ConversationId, payload.SenderId, payload.RecipientId, payload.Status));
        Assert.DoesNotContain(SecretText, row.Payload);
        Assert.Null(row.DispatchedAt);
    }

    [Fact]
    public async Task SendingToSomeoneOnline_WritesSentAndDeliveredEvents()
    {
        _presence.UserConnected(_bob.Id, "tab");

        await Controller(_alice).SendMessage(_conversation.Id, new SendDirectMessageRequest { Content = SecretText }, CancellationToken.None);

        Assert.Equal(new[] { "direct.message.sent", "direct.message.delivered" }, Events().Select(e => e.Row.Type).ToArray());
        Assert.All(Events(), e => Assert.DoesNotContain(SecretText, e.Row.Payload));
        Assert.Equal("delivered", Events()[1].Payload.Status);
    }

    [Fact]
    public async Task MarkingRead_WritesAReadEvent_AuthorIsThePeer_RecipientIsTheReader()
    {
        _db.DirectMessages.AddRange(
            new DirectMessage { ConversationId = _conversation.Id, SenderId = _alice.Id, Content = SecretText },
            new DirectMessage { ConversationId = _conversation.Id, SenderId = _alice.Id, Content = SecretText + " 2" });
        _db.SaveChanges();

        await Controller(_bob).MarkRead(_conversation.Id, CancellationToken.None);

        var (row, payload) = Assert.Single(Events());
        Assert.Equal("direct.message.read", row.Type);
        Assert.Equal(_db.DirectMessages.Select(m => m.Id).OrderBy(i => i).ToArray(), payload.MessageIds);
        Assert.Equal((_alice.Id, _bob.Id), (payload.SenderId, payload.RecipientId));
        Assert.DoesNotContain(SecretText, row.Payload);
    }

    [Fact]
    public async Task MarkingReadWhenNothingIsUnread_WritesNoEvent()
    {
        await Controller(_bob).MarkRead(_conversation.Id, CancellationToken.None);

        Assert.Empty(_db.OutboxMessages);
    }

    [Fact]
    public async Task DeletingAMessage_WritesADeletedEvent_OnlyOnce_EvenIfRepeated()
    {
        var message = new DirectMessage { ConversationId = _conversation.Id, SenderId = _alice.Id, Content = SecretText };
        _db.DirectMessages.Add(message);
        _db.SaveChanges();

        await Controller(_alice).DeleteMessage(_conversation.Id, message.Id, CancellationToken.None);
        await Controller(_alice).DeleteMessage(_conversation.Id, message.Id, CancellationToken.None); // idempotent: no second event

        var (row, payload) = Assert.Single(Events());
        Assert.Equal("direct.message.deleted", row.Type);
        Assert.Equal(new[] { message.Id }, payload.MessageIds);
        Assert.Equal((_alice.Id, _bob.Id), (payload.SenderId, payload.RecipientId));
        Assert.DoesNotContain(SecretText, row.Payload);
    }

    [Fact]
    public async Task ConnectingAfterMessagesWereSent_WritesOneDeliveredEvent_PerSenderAndConversation()
    {
        _db.DirectMessages.AddRange(
            new DirectMessage { ConversationId = _conversation.Id, SenderId = _alice.Id, Content = "one" },
            new DirectMessage { ConversationId = _conversation.Id, SenderId = _alice.Id, Content = "two" });
        _db.SaveChanges();

        await new DirectDeliveryService(_db, _notifier.Object, new OutboxWriter(_db)).MarkPendingDeliveredAsync(_bob.Id);

        var (row, payload) = Assert.Single(Events());
        Assert.Equal("direct.message.delivered", row.Type);
        Assert.Equal(2, payload.MessageIds.Count);
        Assert.Equal((_alice.Id, _bob.Id), (payload.SenderId, payload.RecipientId));
    }

    [Fact]
    public async Task WithTheOutboxOff_NoRowsAreWritten_AndTheChatStillWorks()
    {
        var result = await Controller(_alice, new NoOpOutbox()).SendMessage(_conversation.Id, new SendDirectMessageRequest { Content = "hi" }, CancellationToken.None);

        Assert.NotNull(result.Result);
        Assert.Single(_db.DirectMessages);
        Assert.Empty(_db.OutboxMessages);
    }

    [Fact]
    public async Task IfTheChangeFailsToSave_NoEventIsWritten_AndTheQueuedEventIsKeptForARetry()
    {
        var job = new AiReplyJob { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), UserId = _alice.Id, ClaimToken = Guid.NewGuid() };
        _db.ChatSessions.Add(new ChatSession { Id = job.SessionId, UserId = _alice.Id, Title = "t" });
        var m = new Message { SessionId = job.SessionId, Role = MessageRole.User, Content = "q" };
        _db.Messages.Add(m);
        _db.SaveChanges();
        job.UserMessageId = m.Id;
        _db.AiReplyJobs.Add(job);
        _db.SaveChanges();
        // Someone else changes the row first, so this context's save conflicts.
        job.ClaimToken = Guid.NewGuid();
        _db.Entry(job).Property(j => j.ClaimToken).OriginalValue = Guid.NewGuid();
        _db.QueueEvent("test.event", () => new { x = 1 });

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _db.SaveChangesAsync());

        Assert.Empty(_db.OutboxMessages); // the event never exists without its change
    }

    [Fact]
    public async Task QueuedEvents_AreWrittenOnce_NotAgainOnTheNextSave()
    {
        _db.QueueEvent("test.event", () => new { x = 1 });
        _db.Users.Add(new User { Name = "C", Email = "c@example.com", PasswordHash = "x" });
        await _db.SaveChangesAsync();
        _db.Users.Add(new User { Name = "D", Email = "d@example.com", PasswordHash = "x" });
        await _db.SaveChangesAsync();

        Assert.Single(_db.OutboxMessages);
    }

    [Fact]
    public async Task APlainSave_WithNoQueuedEvents_BehavesAsBefore()
    {
        _db.Users.Add(new User { Name = "C", Email = "c@example.com", PasswordHash = "x" });

        var written = await _db.SaveChangesAsync();

        Assert.Equal(1, written);
        Assert.Empty(_db.OutboxMessages);
    }

    [Fact]
    public void EventPayloads_SortTheirIds_AndHaveTheAgreedJsonShape()
    {
        var payload = DirectEvents.Payload(Guid.Parse("11111111-1111-1111-1111-111111111111"), new long[] { 9, 3, 5 }, 1, 2, "read", new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(payload, DirectEvents.Json);

        Assert.Equal("{\"conversationId\":\"11111111-1111-1111-1111-111111111111\",\"messageIds\":[3,5,9],\"senderId\":1,\"recipientId\":2,\"status\":\"read\",\"at\":\"2026-10-23T08:00:00Z\"}", json);
    }
}

/// <summary>Publishing outbox rows (Phase 19): order, claims, leases, retries after a failure, clean-up. The broker is faked.</summary>
public class OutboxDispatcherTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly Mock<IOutboxPublisher> _publisher = new();
    private readonly List<long> _published = new();
    private readonly AppDbContext _db;

    public OutboxDispatcherTests()
    {
        _db = NewContext();
        _publisher.Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, CancellationToken>((row, _) => { _published.Add(row.Id); return Task.CompletedTask; });
    }

    public void Dispose() => _db.Dispose();

    private AppDbContext NewContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName, _root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning));
        if (interceptors.Length > 0) { builder.AddInterceptors(interceptors); }
        return new AppDbContext(builder.Options);
    }

    private OutboxDispatcher Dispatcher(int batch = 50, int leaseSeconds = 60, int retentionHours = 24, AppDbContext? scopedDb = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => scopedDb ?? NewContext());
        var provider = services.BuildServiceProvider();
        return new OutboxDispatcher(_publisher.Object, provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { BatchSize = batch, LeaseSeconds = leaseSeconds, RetentionHours = retentionHours }),
            _clock, NullLogger<OutboxDispatcher>.Instance);
    }

    private OutboxMessage Row(string type = "direct.message.sent", DateTime? dispatchedAt = null, DateTime? claimedAt = null)
    {
        var row = new OutboxMessage { Type = type, Payload = "{}", ClaimToken = Guid.NewGuid(), DispatchedAt = dispatchedAt, ClaimedAt = claimedAt };
        _db.OutboxMessages.Add(row);
        _db.SaveChanges();
        return row;
    }

    private OutboxMessage Fresh(long id) { using var c = NewContext(); return c.OutboxMessages.Single(o => o.Id == id); }

    [Fact]
    public async Task PublishesPendingRowsInIdOrder_AndMarksThemDispatched()
    {
        var a = Row(); var b = Row(); var c = Row();

        var count = await Dispatcher().DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, _published);
        Assert.All(new[] { a.Id, b.Id, c.Id }, id => Assert.NotNull(Fresh(id).DispatchedAt));
    }

    [Fact]
    public async Task NothingToDo_PublishesNothing()
    {
        Assert.Equal(0, await Dispatcher().DispatchBatchAsync(CancellationToken.None));
        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RespectsTheBatchSize_AndTakesTheRestNextRound()
    {
        for (var i = 0; i < 5; i++) { Row(); }

        Assert.Equal(2, await Dispatcher(batch: 2).DispatchBatchAsync(CancellationToken.None));
        Assert.Equal(2, await Dispatcher(batch: 2).DispatchBatchAsync(CancellationToken.None));
        Assert.Equal(1, await Dispatcher(batch: 2).DispatchBatchAsync(CancellationToken.None));
        Assert.Equal(5, _published.Distinct().Count());
    }

    [Fact]
    public async Task APublishThatFails_StopsTheRound_ReleasesTheRow_CountsTheAttempt_AndNeverSkipsAhead()
    {
        var a = Row(); var b = Row(); var c = Row();
        _publisher.Setup(p => p.PublishAsync(It.Is<OutboxMessage>(r => r.Id == b.Id), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("broker down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatcher().DispatchBatchAsync(CancellationToken.None));

        Assert.NotNull(Fresh(a.Id).DispatchedAt);
        var failed = Fresh(b.Id);
        Assert.Null(failed.DispatchedAt);
        Assert.Null(failed.ClaimedAt);          // released, so the next round tries it again at once
        Assert.Equal(1, failed.Attempts);
        Assert.Null(Fresh(c.Id).DispatchedAt);  // c must not overtake b
        Assert.DoesNotContain(c.Id, _published);
    }

    [Fact]
    public async Task AfterTheBrokerRecovers_TheNextRoundPublishesTheRest_InOrder()
    {
        var a = Row(); var b = Row();
        var down = true;
        _publisher.Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, CancellationToken>((row, _) => down ? throw new InvalidOperationException("down") : Task.Run(() => _published.Add(row.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatcher().DispatchBatchAsync(CancellationToken.None));
        down = false;

        await Dispatcher().DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(new[] { a.Id, b.Id }, _published);
    }

    [Fact]
    public async Task ARowClaimedByALiveReplica_IsSkipped_UntilItsLeaseRunsOut()
    {
        var row = Row(claimedAt: Start.UtcDateTime);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await Dispatcher(leaseSeconds: 60).DispatchBatchAsync(CancellationToken.None));

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await Dispatcher(leaseSeconds: 60).DispatchBatchAsync(CancellationToken.None));

        Assert.Equal(new[] { row.Id }, _published);
    }

    [Fact]
    public async Task AlreadyDispatchedRows_AreNeverPublishedAgain()
    {
        Row(dispatchedAt: Start.UtcDateTime);

        Assert.Equal(0, await Dispatcher().DispatchBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwoReplicasRacingForTheSameRow_OnlyOnePublishesIt()
    {
        var row = Row();
        // A rival replica claims the row in the instant this one saves its own claim.
        var racing = NewContext(new RivalOutboxClaim(_dbName, _root, row.Id));

        var count = await Dispatcher(scopedDb: racing).DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task EventsPublishedBeforeTheRetentionPeriod_AreKept_AndOlderOnesAreDeletedByTheLoop()
    {
        var recent = Row(dispatchedAt: Start.UtcDateTime.AddHours(-1));
        var old = Row(dispatchedAt: Start.UtcDateTime.AddHours(-25));
        var pending = Row();
        var dispatcher = Dispatcher(retentionHours: 24);
        using var cts = new CancellationTokenSource();

        var run = dispatcher.StartAsync(cts.Token);
        await run;
        await Task.Delay(1500); // the loop runs a round immediately: publish the pending row, then clean up
        await dispatcher.StopAsync(CancellationToken.None);

        using var check = NewContext();
        Assert.Contains(check.OutboxMessages, o => o.Id == recent.Id);
        Assert.DoesNotContain(check.OutboxMessages, o => o.Id == old.Id);
        Assert.NotNull(check.OutboxMessages.Single(o => o.Id == pending.Id).DispatchedAt);
    }

    private sealed class RivalOutboxClaim : SaveChangesInterceptor
    {
        private readonly string _dbName;
        private readonly InMemoryDatabaseRoot _root;
        private readonly long _id;
        private bool _done;

        public RivalOutboxClaim(string dbName, InMemoryDatabaseRoot root, long id) { _dbName = dbName; _root = root; _id = id; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                using var rival = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName, _root).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning)).Options);
                var row = rival.OutboxMessages.Single(o => o.Id == _id);
                row.ClaimedAt = DateTime.UtcNow; row.ClaimToken = Guid.NewGuid();
                rival.SaveChanges();
            }
            return ValueTask.FromResult(result);
        }
    }
}

/// <summary>The version-1 subscriber (Phase 19): validates, de-duplicates, logs and counts events.</summary>
public class DirectEventHandlerTests
{
    private static byte[] Body(string status = "sent", params long[] ids) =>
        JsonSerializer.SerializeToUtf8Bytes(DirectEvents.Payload(Guid.NewGuid(), ids.Length == 0 ? new long[] { 1 } : ids, 1, 2, status, DateTime.UtcNow), DirectEvents.Json);

    private static DirectEventHandler Handler(IEventDeduplicator? dedupe = null) =>
        new(dedupe ?? new InMemoryEventDeduplicator(), NullLogger<DirectEventHandler>.Instance);

    [Fact]
    public async Task AValidEvent_IsHandled_AndCounted()
    {
        var counted = new List<(long Value, string? Type)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == DirectEventHandler.MeterName) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "aichat.direct_events") { counted.Add((value, tags.ToArray().FirstOrDefault(t => t.Key == "type").Value as string)); }
        });
        listener.Start();

        var result = await Handler().HandleAsync("direct.message.sent", $"counted-{Guid.NewGuid()}", Body());

        Assert.Equal(DirectEventResult.Handled, result);
        Assert.Contains((1L, "direct.message.sent"), counted);
    }

    [Fact]
    public async Task ARepeatedDelivery_IsRecognisedByItsOutboxId_AndIgnored()
    {
        var handler = Handler();
        var body = Body();

        Assert.Equal(DirectEventResult.Handled, await handler.HandleAsync("direct.message.sent", "42", body));
        Assert.Equal(DirectEventResult.Duplicate, await handler.HandleAsync("direct.message.sent", "42", body));
        Assert.Equal(DirectEventResult.Handled, await handler.HandleAsync("direct.message.sent", "43", body));
    }

    [Fact]
    public async Task TwoSubscribersSharingOneDeduplicator_HandleAnEventOnlyOnce_LikeTwoReplicasWithRedis()
    {
        var shared = new InMemoryEventDeduplicator();
        var replicaA = Handler(shared);
        var replicaB = Handler(shared);

        Assert.Equal(DirectEventResult.Handled, await replicaA.HandleAsync("direct.message.sent", "7", Body()));
        Assert.Equal(DirectEventResult.Duplicate, await replicaB.HandleAsync("direct.message.sent", "7", Body()));
    }

    [Theory]
    [InlineData(null, "1", "{}")]
    [InlineData("", "1", "{}")]
    [InlineData("direct.message.sent", null, "{\"conversationId\":\"11111111-1111-1111-1111-111111111111\",\"messageIds\":[1]}")]
    [InlineData("direct.message.sent", "1", "not json")]
    [InlineData("direct.message.sent", "1", "{\"conversationId\":\"00000000-0000-0000-0000-000000000000\",\"messageIds\":[1]}")]
    [InlineData("direct.message.sent", "1", "{\"conversationId\":\"11111111-1111-1111-1111-111111111111\",\"messageIds\":[]}")]
    public async Task AnEventMissingItsTypeIdOrPayload_IsRejected_ForTheDeadLetterQueue(string? type, string? id, string json)
    {
        Assert.Equal(DirectEventResult.Invalid, await Handler().HandleAsync(type, id, Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task AnInvalidEvent_IsNotRememberedAsSeen()
    {
        var handler = Handler();

        Assert.Equal(DirectEventResult.Invalid, await handler.HandleAsync("direct.message.sent", "7", Encoding.UTF8.GetBytes("nope")));
        Assert.Equal(DirectEventResult.Handled, await handler.HandleAsync("direct.message.sent", "7", Body()));
    }

    [Fact]
    public void TheTopologyUsesTheAgreedNames()
    {
        Assert.Equal("direct.events", DirectEventsTopology.Exchange);
        Assert.Equal("direct.events.log", DirectEventsTopology.LogQueue);
        Assert.Equal("direct.events.dead", DirectEventsTopology.DeadQueue);
    }
}

public class EventDeduplicatorTests
{
    [Fact]
    public async Task InMemory_ReportsFirstSightingOnce()
    {
        var d = new InMemoryEventDeduplicator();

        Assert.True(await d.TryMarkSeenAsync("a"));
        Assert.False(await d.TryMarkSeenAsync("a"));
        Assert.True(await d.TryMarkSeenAsync("b"));
    }

    [Fact]
    public async Task InMemory_IsBounded_ForgettingTheOldestFirst()
    {
        var d = new InMemoryEventDeduplicator(capacity: 3);
        foreach (var id in new[] { "1", "2", "3", "4" }) { await d.TryMarkSeenAsync(id); }

        Assert.True(await d.TryMarkSeenAsync("1"));   // forgotten: memory stays bounded
        Assert.False(await d.TryMarkSeenAsync("4"));  // still remembered
    }

    [Fact]
    public async Task Redis_FailsOpen_WhenRedisIsDown_SoAnEventIsNeverLostToTheDeduplicator()
    {
        var redis = new Mock<StackExchange.Redis.IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Throws(new StackExchange.Redis.RedisConnectionException(StackExchange.Redis.ConnectionFailureType.UnableToConnect, "down"));
        var d = new RedisEventDeduplicator(redis.Object, NullLogger<RedisEventDeduplicator>.Instance);

        Assert.True(await d.TryMarkSeenAsync("any"));
    }

    [Fact]
    public void Redis_KeysAreNamespaced_AndEachEventHasItsOwn()
    {
        Assert.Equal("dedupe:direct-event:42", RedisEventDeduplicator.KeyFor("42"));
    }
}

public class OutboxWiringTests
{
    private static ServiceCollection Services(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmailDelivery(configuration);
        services.AddBrokerFeatures(configuration);
        return services;
    }

    private static readonly (string, string?)[] Broker = { ("RabbitMq:Host", "rabbitmq"), ("RabbitMq:Username", "u"), ("RabbitMq:Password", "p") };

    [Fact]
    public async Task WithABroker_TheOutboxWriter_Dispatcher_AndSubscriber_AreRegistered()
    {
        var services = Services(Broker);
        await using var provider = services.BuildServiceProvider();

        Assert.Contains(services, d => d.ServiceType == typeof(IOutbox) && d.ImplementationType == typeof(OutboxWriter));
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is OutboxDispatcher);
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is DirectEventsConsumer);
        Assert.IsType<RabbitMqOutboxPublisher>(provider.GetRequiredService<IOutboxPublisher>());
    }

    [Fact]
    public async Task WithoutABroker_NothingIsWritten_AndNoDispatcherRuns()
    {
        var services = Services();
        await using var provider = services.BuildServiceProvider();

        Assert.Contains(services, d => d.ServiceType == typeof(IOutbox) && d.ImplementationType == typeof(NoOpOutbox));
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is OutboxDispatcher or DirectEventsConsumer);
    }

    [Fact]
    public async Task WithABroker_ButTheOutboxSwitchedOff_NothingIsWritten()
    {
        var services = Services(Broker.Append(("Outbox:Enabled", "false")).ToArray());
        await using var provider = services.BuildServiceProvider();

        Assert.Contains(services, d => d.ServiceType == typeof(IOutbox) && d.ImplementationType == typeof(NoOpOutbox));
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is OutboxDispatcher);
    }

    [Fact]
    public void TheOutboxDefaults_AreSensible()
    {
        var defaults = new OutboxOptions();

        Assert.True(defaults.Enabled);
        Assert.True(defaults.LeaseSeconds >= 10 * defaults.PollMilliseconds / 1000, "the lease must be much longer than one poll, or live claims would be taken over");
        Assert.True(defaults.RetentionHours >= 1);
    }
}
