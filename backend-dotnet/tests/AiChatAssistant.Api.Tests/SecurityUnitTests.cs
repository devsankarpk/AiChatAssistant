using System.Security.Claims;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Security;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Hubs;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 21 building blocks without a web server: the counters, the login lockout, the options and the typing throttle.</summary>
public class RateLimitCounterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly ManualTimeProvider _clock = new(Start);
    private InMemoryRateLimitCounter Create() => new(_clock);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task EachHitCountsOne_AndTheFirstStartsTheWindow()
    {
        var counter = Create();

        Assert.Equal(1, (await counter.HitAsync("k", Minute)).Count);
        _clock.Advance(TimeSpan.FromSeconds(20));
        var third = await counter.HitAsync("k", Minute);
        Assert.Equal(2, third.Count);
        Assert.Equal(TimeSpan.FromSeconds(40), third.Remaining); // the window did not restart on the second hit
    }

    [Fact]
    public async Task WhenTheWindowEnds_CountingStartsAgainFromOne()
    {
        var counter = Create();
        await counter.HitAsync("k", Minute);
        await counter.HitAsync("k", Minute);

        _clock.Advance(Minute);

        Assert.Equal(1, (await counter.HitAsync("k", Minute)).Count);
    }

    [Fact]
    public async Task KeysAreCountedSeparately()
    {
        var counter = Create();
        await counter.HitAsync("a", Minute);
        await counter.HitAsync("a", Minute);

        Assert.Equal(1, (await counter.HitAsync("b", Minute)).Count);
    }

    [Fact]
    public async Task PeekReadsWithoutCounting_AndIsZeroForAnUnknownOrEndedKey()
    {
        var counter = Create();
        Assert.Equal(0, (await counter.PeekAsync("k")).Count);
        await counter.HitAsync("k", Minute);

        Assert.Equal(1, (await counter.PeekAsync("k")).Count);
        Assert.Equal(1, (await counter.PeekAsync("k")).Count);
        _clock.Advance(Minute);
        Assert.Equal(0, (await counter.PeekAsync("k")).Count);
    }

    [Fact]
    public async Task ResetForgetsTheKey()
    {
        var counter = Create();
        await counter.HitAsync("k", Minute);

        await counter.ResetAsync("k");

        Assert.Equal(0, (await counter.PeekAsync("k")).Count);
        await counter.ResetAsync("never-seen"); // not an error
    }

    [Fact]
    public async Task ManyParallelHits_AreAllCounted()
    {
        var counter = Create();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => counter.HitAsync("k", Minute)));

        Assert.Equal(200, (await counter.PeekAsync("k")).Count);
    }

    [Fact]
    public async Task EndedWindowsAreSweptOut_SoOneOffClientsDoNotAccumulate()
    {
        var counter = Create();
        for (var i = 0; i < 50; i++) await counter.HitAsync($"ip-{i}", Minute);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await counter.HitAsync("trigger", Minute); // any hit after a minute triggers the sweep

        Assert.Equal(0, (await counter.PeekAsync("ip-1")).Count);
        Assert.Equal(1, (await counter.PeekAsync("trigger")).Count);
    }
}

public class ResilientRateLimitCounterTests
{
    private sealed class Spy : IRateLimitCounter
    {
        public bool Fail;
        public int Hits, Peeks, Resets;
        public Task<RateLimitCount> HitAsync(string key, TimeSpan window) { Check(); Hits++; return Task.FromResult(new RateLimitCount(Hits, window)); }
        public Task<RateLimitCount> PeekAsync(string key) { Check(); Peeks++; return Task.FromResult(new RateLimitCount(Hits, TimeSpan.Zero)); }
        public Task ResetAsync(string key) { Check(); Resets++; return Task.CompletedTask; }
        private void Check() { if (Fail) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down (test)"); }
    }

    private readonly Spy _redis = new(), _local = new();
    private ResilientRateLimitCounter Create() => new(_redis, _local, NullLogger<ResilientRateLimitCounter>.Instance);

    [Fact]
    public async Task WhileRedisAnswers_ItIsUsedAndTheLocalCounterIsNot()
    {
        await Create().HitAsync("k", TimeSpan.FromMinutes(1));

        Assert.Equal(1, _redis.Hits);
        Assert.Equal(0, _local.Hits);
    }

    [Fact]
    public async Task WhenRedisIsDown_ItCountsLocally_AndNeverThrows()
    {
        _redis.Fail = true;

        var hit = await Create().HitAsync("k", TimeSpan.FromMinutes(1));
        var peek = await Create().PeekAsync("k");

        Assert.Equal(1, hit.Count);
        Assert.Equal(1, _local.Hits);
        Assert.Equal(1, _local.Peeks);
        Assert.Equal(1, peek.Count);
    }

    [Fact]
    public async Task ResetClearsBothSides_AndSurvivesRedisBeingDown()
    {
        _redis.Fail = true;

        await Create().ResetAsync("k");

        Assert.Equal(1, _local.Resets);
    }
}

public class LoginLockoutTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly ManualTimeProvider _clock = new(Start);

    private (LoginLockout Lockout, InMemoryRateLimitCounter Counter) Create(int max = 5, int minutes = 15, bool enabled = true)
    {
        var counter = new InMemoryRateLimitCounter(_clock);
        return (new LoginLockout(counter, Options.Create(new LockoutOptions { MaxFailedAttempts = max, LockoutMinutes = minutes, Enabled = enabled })), counter);
    }

    [Fact]
    public async Task TheFailureThatReachesTheLimit_LocksTheEmail()
    {
        var (lockout, _) = Create(max: 3);

        Assert.False((await lockout.RecordFailureAsync("a@example.com")).Locked);
        Assert.False((await lockout.RecordFailureAsync("a@example.com")).Locked);
        var third = await lockout.RecordFailureAsync("a@example.com");

        Assert.True(third.Locked);
        Assert.True((await lockout.CheckAsync("a@example.com")).Locked);
    }

    [Fact]
    public async Task ALockedEmail_SaysHowLongIsLeft_AndUnlocksWhenTheWindowEnds()
    {
        var (lockout, _) = Create(max: 2, minutes: 10);
        await lockout.RecordFailureAsync("a@example.com");
        await lockout.RecordFailureAsync("a@example.com");
        _clock.Advance(TimeSpan.FromMinutes(4));

        var state = await lockout.CheckAsync("a@example.com");
        Assert.True(state.Locked);
        Assert.Equal(TimeSpan.FromMinutes(6), state.RetryAfter);

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False((await lockout.CheckAsync("a@example.com")).Locked);
    }

    [Fact]
    public async Task OneEmailLockingDoesNotLockAnother()
    {
        var (lockout, _) = Create(max: 1);
        await lockout.RecordFailureAsync("a@example.com");

        Assert.False((await lockout.CheckAsync("b@example.com")).Locked);
    }

    [Fact]
    public async Task ASuccessfulLogin_ClearsTheCount()
    {
        var (lockout, _) = Create(max: 3);
        await lockout.RecordFailureAsync("a@example.com");
        await lockout.RecordFailureAsync("a@example.com");

        await lockout.ClearAsync("a@example.com");

        Assert.False((await lockout.RecordFailureAsync("a@example.com")).Locked);
        Assert.False((await lockout.RecordFailureAsync("a@example.com")).Locked);
    }

    [Fact]
    public async Task CaseAndSpacesInTheEmailDoNotGiveAFreshCount()
    {
        var (lockout, _) = Create(max: 2);
        await lockout.RecordFailureAsync("Ada@Example.com");

        var second = await lockout.RecordFailureAsync("  ada@example.com ");

        Assert.True(second.Locked);
    }

    [Fact]
    public async Task TheStoreNeverSeesTheEmailAddress()
    {
        var keys = new List<string>();
        var spy = new Mock<IRateLimitCounter>();
        spy.Setup(c => c.HitAsync(It.IsAny<string>(), It.IsAny<TimeSpan>())).Callback<string, TimeSpan>((k, _) => keys.Add(k)).ReturnsAsync(new RateLimitCount(1, TimeSpan.FromMinutes(1)));
        var lockout = new LoginLockout(spy.Object, Options.Create(new LockoutOptions()));

        await lockout.RecordFailureAsync("secret.person@example.com");

        var key = Assert.Single(keys);
        Assert.DoesNotContain("secret", key);
        Assert.DoesNotContain("example.com", key);
        Assert.StartsWith("lockout:", key);
    }

    [Fact]
    public async Task WhenSwitchedOff_NothingIsEverLocked_AndNothingIsCounted()
    {
        var (lockout, counter) = Create(max: 1, enabled: false);

        Assert.False((await lockout.RecordFailureAsync("a@example.com")).Locked);
        Assert.False((await lockout.CheckAsync("a@example.com")).Locked);
        Assert.Equal(0, (await counter.PeekAsync("anything")).Count);
    }
}

public class SecurityConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Theory]
    [InlineData("RateLimiting:AuthPerMinute", "0")]
    [InlineData("RateLimiting:ChatPerMinute", "-1")]
    [InlineData("RateLimiting:WindowSeconds", "0")]
    [InlineData("Lockout:MaxFailedAttempts", "0")]
    [InlineData("Lockout:LockoutMinutes", "0")]
    public void ANonsenseLimit_StopsTheAppAtStartup_NamingTheSetting(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSecurityHardening(Config((key, value))));

        Assert.Contains(key.Split(':')[0], ex.Message);
    }

    [Fact]
    public void ASwitchedOffFeature_IsNotValidated()
    {
        new ServiceCollection().AddSecurityHardening(Config(("RateLimiting:Enabled", "false"), ("RateLimiting:AuthPerMinute", "0")));
    }

    [Fact]
    public void TheDefaults_AreTenAuthAndTwentyChatPerMinute_FiveFailuresFifteenMinutes()
    {
        Assert.Equal(10, new RateLimitOptions().AuthPerMinute);
        Assert.Equal(20, new RateLimitOptions().ChatPerMinute);
        Assert.Equal(60, new RateLimitOptions().WindowSeconds);
        Assert.Equal(5, new LockoutOptions().MaxFailedAttempts);
        Assert.Equal(15, new LockoutOptions().LockoutMinutes);
    }

    [Fact]
    public void WithoutRedis_CountsLiveInThisProcess()
    {
        var provider = new ServiceCollection().AddLogging().AddSecurityHardening(Config()).BuildServiceProvider();

        Assert.IsType<InMemoryRateLimitCounter>(provider.GetRequiredService<IRateLimitCounter>());
    }

    [Fact]
    public void WithRedis_CountsAreSharedThroughRedis_ButFallBackToMemoryWhenItIsDown()
    {
        var services = new ServiceCollection().AddLogging().AddSecurityHardening(Config());
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());

        Assert.IsType<ResilientRateLimitCounter>(services.BuildServiceProvider().GetRequiredService<IRateLimitCounter>());
    }
}

public class TypingThrottleTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IDirectNotifier> _notifier = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly User _alice, _bob;
    private readonly DirectConversation _conversation;

    public TypingThrottleTests()
    {
        _alice = new User { Name = "Alice", Email = "alice@example.com", PasswordHash = "x" };
        _bob = new User { Name = "Bob", Email = "bob@example.com", PasswordHash = "x" };
        _db.Users.AddRange(_alice, _bob);
        _db.SaveChanges();
        _conversation = new DirectConversation { Id = Guid.NewGuid(), UserLowId = Math.Min(_alice.Id, _bob.Id), UserHighId = Math.Max(_alice.Id, _bob.Id) };
        _db.DirectConversations.Add(_conversation);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DirectHub Connection(User who, string connectionId)
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(ClaimsPrincipalFactory.Create(who.Id));
        context.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());
        return new DirectHub(new InMemoryPresenceTracker(), Mock.Of<IDirectDeliveryService>(), _notifier.Object, new PresenceAnnouncer(_db, _notifier.Object), _db, NullLogger<DirectHub>.Instance, _clock)
        { Context = context.Object, Groups = Mock.Of<IGroupManager>() };
    }

    private void VerifyForwarded(int times) =>
        _notifier.Verify(n => n.PeerTypingAsync(It.IsAny<int>(), _conversation.Id, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    [Fact]
    public async Task ARapidSecondSignalFromTheSameConnection_IsDropped()
    {
        var hub = Connection(_alice, "a1");

        await hub.Typing(_conversation.Id);
        await hub.Typing(_conversation.Id);
        await hub.Typing(_conversation.Id);

        VerifyForwarded(1);
    }

    [Fact]
    public async Task AfterTheMinimumInterval_TheNextSignalGoesThrough()
    {
        var hub = Connection(_alice, "a1");
        await hub.Typing(_conversation.Id);

        _clock.Advance(DirectHub.TypingMinInterval - TimeSpan.FromMilliseconds(1));
        await hub.Typing(_conversation.Id);
        VerifyForwarded(1);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await hub.Typing(_conversation.Id);
        VerifyForwarded(2);
    }

    [Fact]
    public async Task EachConnectionHasItsOwnAllowance()
    {
        await Connection(_alice, "a1").Typing(_conversation.Id);
        await Connection(_bob, "b1").Typing(_conversation.Id);

        VerifyForwarded(2);
    }

    [Fact]
    public async Task TheBrowsersNormalRate_OneEveryThreeSeconds_IsNeverDropped()
    {
        var hub = Connection(_alice, "a1");

        for (var i = 0; i < 5; i++)
        {
            await hub.Typing(_conversation.Id);
            _clock.Advance(TimeSpan.FromSeconds(3));
        }

        VerifyForwarded(5);
    }
}
