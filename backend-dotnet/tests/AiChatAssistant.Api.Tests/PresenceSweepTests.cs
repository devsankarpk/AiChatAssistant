using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Realtime;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// Backlog item L-04 (Phase 16 follow-up): a replica that is killed runs no disconnect code, so nobody saved the person's last-seen
/// time or told their contacts. The sweeper finds people whose every presence mark lapsed and does what a clean disconnect would have.
/// The Redis scripts themselves are checked against a real Redis and a really killed replica in the Docker run (TESTING.md section 21).
/// </summary>
public class PresenceSweepTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeStore _store = new();
    private readonly ManualTimeProvider _clock = new(Start);

    private RedisPresenceTracker Create(int ttl = 45, int grace = 20) =>
        new(_store, Options.Create(new PresenceOptions { TtlSeconds = ttl, SweepGraceSeconds = grace }), _clock, NullLogger<RedisPresenceTracker>.Instance);

    [Fact]
    public async Task ReturnsThePeopleTheStoreSaysAreGone_WithTheirLastHeartbeatAsUtc()
    {
        _store.Lapsed.Add((7, Start.AddSeconds(-50)));

        var swept = await Create().SweepLapsedAsync();

        var person = Assert.Single(swept);
        Assert.Equal(7, person.UserId);
        Assert.Equal(Start.AddSeconds(-50).UtcDateTime, person.LastSeenUtc);
        Assert.Equal(DateTimeKind.Utc, person.LastSeenUtc.Kind);
    }

    [Fact]
    public async Task AsksTheStoreWithTheClockTheGraceAndTheTtl()
    {
        _clock.Advance(TimeSpan.FromMinutes(3));

        await Create(ttl: 30, grace: 12).SweepLapsedAsync();

        Assert.Equal((Start.AddMinutes(3), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30)), Assert.Single(_store.Sweeps));
    }

    [Fact]
    public async Task NeverAnnouncesSomeoneThisServerStillHoldsALiveConnectionFor()
    {
        var tracker = Create();
        await tracker.UserConnectedAsync(7, "tab-here");   // alive on this very server; only their Redis mark lapsed (a Redis blip)
        _store.Lapsed.Add((7, Start.AddSeconds(-60)));
        _store.Lapsed.Add((8, Start.AddSeconds(-60)));      // a stranger's server died

        var swept = await tracker.SweepLapsedAsync();

        Assert.Equal(new[] { 8 }, swept.Select(s => s.UserId));
    }

    [Fact]
    public async Task AfterRedisWasUnreachable_ItHoldsOffForTheGracePeriodPlusAHeartbeat_ThenSweepsAgain()
    {
        var tracker = new RedisPresenceTracker(_store, Options.Create(new PresenceOptions { TtlSeconds = 45, HeartbeatSeconds = 15, SweepGraceSeconds = 20 }), _clock, NullLogger<RedisPresenceTracker>.Instance);
        _store.Fail = true;
        await tracker.OnlineAmongAsync(new[] { 1 });          // a request noticed the outage
        _store.Fail = false;
        _store.Lapsed.Add((8, Start.AddSeconds(-300)));       // every mark looks long dead because Redis was away, not because anyone crashed

        Assert.Empty(await tracker.SweepLapsedAsync());       // right after the outage: do nothing
        _clock.Advance(TimeSpan.FromSeconds(34));
        Assert.Empty(await tracker.SweepLapsedAsync());       // still inside 20 s + 15 s
        Assert.Empty(_store.Sweeps);                          // and the store was not even asked
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 8 }, (await tracker.SweepLapsedAsync()).Select(s => s.UserId));
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("disconnect")]
    [InlineData("refresh")]
    [InlineData("sweep")]
    public async Task AnyFailureToReachRedis_StartsTheHoldOff(string operation)
    {
        var tracker = Create();
        await tracker.UserConnectedAsync(7, "c1");
        _store.Fail = true;
        switch (operation)
        {
            case "connect": await tracker.UserConnectedAsync(9, "c9"); break;
            case "disconnect": await tracker.UserDisconnectedAsync(7, "c1"); break;
            case "refresh": await tracker.RefreshAsync(); break;
            default: await tracker.SweepLapsedAsync(); break;
        }
        _store.Fail = false;
        _store.Lapsed.Add((8, Start.AddSeconds(-300)));

        Assert.Empty(await tracker.SweepLapsedAsync());
    }

    [Fact]
    public async Task NothingLapsed_IsAnEmptyAnswer()
    {
        Assert.Empty(await Create().SweepLapsedAsync());
    }

    [Fact]
    public async Task WhenRedisIsDown_ItReturnsNothingAndDoesNotThrow()
    {
        _store.Fail = true;

        Assert.Empty(await Create().SweepLapsedAsync());
    }

    [Fact]
    public async Task InMemoryPresence_HasNothingToSweep()
    {
        Assert.Empty(await new InMemoryPresenceTracker().SweepLapsedAsync());
    }

    [Fact]
    public void TheDefaults_SweepEveryTenSeconds_AfterAGraceLongerThanOneHeartbeat()
    {
        var options = new PresenceOptions();

        Assert.Equal(10, options.SweepSeconds);
        Assert.Equal(20, options.SweepGraceSeconds);
        Assert.True(options.SweepGraceSeconds > options.HeartbeatSeconds, "a Redis outage of one heartbeat must not look like a crash");
    }

    [Fact]
    public void WithRedis_TheSweeperIsRegistered_WithoutRedis_ItIsNot()
    {
        IConfiguration Config(string redis) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Redis:ConnectionString"] = redis }).Build();

        var shared = new ServiceCollection().AddRealtime(Config("localhost:6379"));
        var local = new ServiceCollection().AddRealtime(Config(""));

        Assert.Contains(shared, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PresenceSweeper));
        Assert.DoesNotContain(local, d => d.ImplementationType == typeof(PresenceSweeper));
    }
}

public class PresenceSweeperTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IDirectNotifier> _notifier = new();
    private readonly Mock<IPresenceTracker> _presence = new();
    private readonly User _bob, _alice, _carol, _stranger;

    public PresenceSweeperTests()
    {
        _bob = new User { Name = "Bob", Email = "bob@example.com", PasswordHash = "x" };
        _alice = new User { Name = "Alice", Email = "alice@example.com", PasswordHash = "x" };
        _carol = new User { Name = "Carol", Email = "carol@example.com", PasswordHash = "x" };
        _stranger = new User { Name = "Stranger", Email = "stranger@example.com", PasswordHash = "x" };
        _db.Users.AddRange(_bob, _alice, _carol, _stranger);
        _db.SaveChanges();
        Talk(_bob, _alice);
        Talk(_bob, _carol);
    }

    public void Dispose() => _db.Dispose();

    private void Talk(User a, User b)
    {
        _db.DirectConversations.Add(new DirectConversation { Id = Guid.NewGuid(), UserLowId = Math.Min(a.Id, b.Id), UserHighId = Math.Max(a.Id, b.Id) });
        _db.SaveChanges();
    }

    private PresenceSweeper Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_notifier.Object);
        services.AddScoped<PresenceAnnouncer>();
        return new PresenceSweeper(_presence.Object, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PresenceOptions()), NullLogger<PresenceSweeper>.Instance);
    }

    private void Lapsed(params LapsedPresence[] people) =>
        _presence.Setup(p => p.SweepLapsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(people);

    [Fact]
    public async Task ACrashedPerson_GetsTheirLastSeenSaved_AndTheirContactsAreToldTheyAreOffline()
    {
        var lastBeat = new DateTime(2026, 10, 25, 8, 59, 0, DateTimeKind.Utc);
        Lapsed(new LapsedPresence(_bob.Id, lastBeat));

        var count = await Create().SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(lastBeat, _db.Users.Single(u => u.Id == _bob.Id).LastSeenAt);
        _notifier.Verify(n => n.PeerPresenceChangedAsync(
            It.Is<IReadOnlyCollection<int>>(peers => peers.OrderBy(x => x).SequenceEqual(new[] { _alice.Id, _carol.Id }.OrderBy(x => x))),
            _bob.Id, false, lastBeat, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnlyPeopleWhoShareAConversationAreTold()
    {
        Lapsed(new LapsedPresence(_bob.Id, DateTime.UtcNow));

        await Create().SweepOnceAsync(CancellationToken.None);

        _notifier.Verify(n => n.PeerPresenceChangedAsync(It.Is<IReadOnlyCollection<int>>(p => p.Contains(_stranger.Id)), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SeveralCrashedPeopleAreEachHandled()
    {
        Lapsed(new LapsedPresence(_bob.Id, DateTime.UtcNow.AddMinutes(-1)), new LapsedPresence(_alice.Id, DateTime.UtcNow.AddMinutes(-2)));

        var count = await Create().SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.NotNull(_db.Users.Single(u => u.Id == _bob.Id).LastSeenAt);
        Assert.NotNull(_db.Users.Single(u => u.Id == _alice.Id).LastSeenAt);
    }

    [Fact]
    public async Task NobodyLapsed_MeansNoDatabaseWriteAndNoPush()
    {
        Lapsed();

        var count = await Create().SweepOnceAsync(CancellationToken.None);

        Assert.Equal(0, count);
        _notifier.VerifyNoOtherCalls();
        Assert.All(_db.Users, u => Assert.Null(u.LastSeenAt));
    }

    [Fact]
    public async Task OneFailedAnnouncement_DoesNotStopTheOthers()
    {
        Lapsed(new LapsedPresence(_bob.Id, DateTime.UtcNow), new LapsedPresence(_alice.Id, DateTime.UtcNow));
        _notifier.SetupSequence(n => n.PeerPresenceChangedAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("push failed"))
            .Returns(Task.CompletedTask);

        var count = await Create().SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, count);
        _notifier.Verify(n => n.PeerPresenceChangedAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AFailingTracker_IsSurvived()
    {
        _presence.Setup(p => p.SweepLapsedAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

        Assert.Equal(0, await Create().SweepOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task APersonWhoNoLongerExists_IsStillAnnounced_WithoutAnError()
    {
        Lapsed(new LapsedPresence(99999, DateTime.UtcNow));

        Assert.Equal(1, await Create().SweepOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARealDisconnectAndTheSweeper_UseTheSameAnnouncer_SoTheyCannotDisagree()
    {
        var announcer = new PresenceAnnouncer(_db, _notifier.Object);
        var when = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

        await announcer.AnnounceOfflineAsync(_bob.Id, when);

        Assert.Equal(when, _db.Users.Single(u => u.Id == _bob.Id).LastSeenAt);
        _notifier.Verify(n => n.PeerPresenceChangedAsync(It.IsAny<IReadOnlyCollection<int>>(), _bob.Id, false, when, It.IsAny<CancellationToken>()), Times.Once);
    }
}
