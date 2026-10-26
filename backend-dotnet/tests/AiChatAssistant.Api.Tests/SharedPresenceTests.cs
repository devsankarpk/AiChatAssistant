using AiChatAssistant.Api.Realtime;
using AiChatAssistant.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AiChatAssistant.Api.Tests;

/// <summary>A presence store the test controls: records what it is asked and can be told to fail like a down Redis.</summary>
internal sealed class FakeStore : IPresenceStore
{
    public bool Fail;
    public bool AddResult = true;
    public bool RemoveResult = true;
    public HashSet<int> Online = new();
    public List<(int UserId, string ConnectionId, DateTimeOffset Now, TimeSpan Ttl)> Adds = new();
    public List<(int UserId, string ConnectionId)> Removes = new();
    public List<IReadOnlyList<(int UserId, string ConnectionId)>> Refreshes = new();
    public TimeSpan? RefreshTtl;

    private void Check() { if (Fail) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down (test)"); }

    public Task<bool> AddAsync(int userId, string connectionId, DateTimeOffset now, TimeSpan ttl)
    {
        Check(); Adds.Add((userId, connectionId, now, ttl)); return Task.FromResult(AddResult);
    }

    public Task<bool> RemoveAsync(int userId, string connectionId, DateTimeOffset now)
    {
        Check(); Removes.Add((userId, connectionId)); return Task.FromResult(RemoveResult);
    }

    public Task<IReadOnlySet<int>> OnlineAmongAsync(IReadOnlyCollection<int> userIds, DateTimeOffset now)
    {
        Check(); return Task.FromResult<IReadOnlySet<int>>(userIds.Where(Online.Contains).ToHashSet());
    }

    public List<(int UserId, DateTimeOffset LastBeat)> Lapsed = new();
    public List<(DateTimeOffset Now, TimeSpan Grace, TimeSpan Ttl)> Sweeps = new();

    public Task<IReadOnlyList<(int UserId, DateTimeOffset LastBeat)>> ExpireLapsedAsync(DateTimeOffset now, TimeSpan grace, TimeSpan ttl, Func<int, bool> skip, int max)
    {
        Check(); Sweeps.Add((now, grace, ttl));
        return Task.FromResult<IReadOnlyList<(int, DateTimeOffset)>>(Lapsed.Where(l => !skip(l.UserId)).ToList());
    }

    public Task RefreshAsync(IReadOnlyList<(int UserId, string ConnectionId)> connections, DateTimeOffset now, TimeSpan ttl)
    {
        Check(); Refreshes.Add(connections.ToList()); RefreshTtl = ttl; return Task.CompletedTask;
    }
}

/// <summary>Presence shared across API replicas (Phase 16). The tracker's decisions and its fallback when Redis is
/// down are tested here with a fake store; the real Redis scripts and a real second replica are verified in the
/// Docker run (TESTING.md section 14).</summary>
public class RedisPresenceTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeStore _store = new();
    private readonly ManualTimeProvider _clock = new(Start);

    private RedisPresenceTracker Create(int ttlSeconds = 45) =>
        new(_store, Options.Create(new PresenceOptions { TtlSeconds = ttlSeconds }), _clock, NullLogger<RedisPresenceTracker>.Instance);

    [Fact]
    public async Task UserConnected_ReportsWhatTheSharedStoreSays_NotJustThisServer()
    {
        var tracker = Create();

        _store.AddResult = false; // the user already has a tab on another replica
        Assert.False(await tracker.UserConnectedAsync(7, "tab-on-this-replica"));

        _store.AddResult = true;
        Assert.True(await tracker.UserConnectedAsync(8, "first-anywhere"));
    }

    [Fact]
    public async Task UserConnected_PassesTheUserConnectionTimeAndTtlToTheStore()
    {
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Create(ttlSeconds: 30).UserConnectedAsync(7, "c1");

        var add = Assert.Single(_store.Adds);
        Assert.Equal((7, "c1", Start.AddSeconds(5), TimeSpan.FromSeconds(30)), add);
    }

    [Fact]
    public async Task UserDisconnected_ReportsWhatTheSharedStoreSays()
    {
        var tracker = Create();
        await tracker.UserConnectedAsync(7, "c1");

        _store.RemoveResult = false; // another replica still holds one of their tabs
        Assert.False(await tracker.UserDisconnectedAsync(7, "c1"));
        Assert.Equal((7, "c1"), Assert.Single(_store.Removes));
    }

    [Fact]
    public async Task OnlineChecks_AskTheSharedStore()
    {
        _store.Online = new HashSet<int> { 2, 3 };
        var tracker = Create();

        Assert.True(await tracker.IsOnlineAsync(2));
        Assert.False(await tracker.IsOnlineAsync(9));
        Assert.Equal(new[] { 2, 3 }, (await tracker.OnlineAmongAsync(new[] { 1, 2, 3 })).OrderBy(x => x));
    }

    [Fact]
    public async Task WhenRedisIsDown_ConnectAndDisconnectFallBackToThisServersOwnView_AndNeverThrow()
    {
        var tracker = Create();
        _store.Fail = true;

        Assert.True(await tracker.UserConnectedAsync(7, "tab-1"));   // first tab on this server
        Assert.False(await tracker.UserConnectedAsync(7, "tab-2"));  // second tab: not "came online" again
        Assert.False(await tracker.UserDisconnectedAsync(7, "tab-1"));
        Assert.True(await tracker.UserDisconnectedAsync(7, "tab-2"));  // last tab here
    }

    [Fact]
    public async Task WhenRedisIsDown_OnlineChecksUseThisServersOwnView_AndNeverThrow()
    {
        var tracker = Create();
        await tracker.UserConnectedAsync(7, "tab-1");
        _store.Fail = true;

        Assert.True(await tracker.IsOnlineAsync(7));
        Assert.False(await tracker.IsOnlineAsync(8));
        Assert.Equal(new[] { 7 }, (await tracker.OnlineAmongAsync(new[] { 7, 8 })).ToArray());
    }

    [Fact]
    public async Task Refresh_SendsEveryConnectionThisServerHolds_WithTheConfiguredTtl()
    {
        var tracker = Create(ttlSeconds: 30);
        await tracker.UserConnectedAsync(1, "a");
        await tracker.UserConnectedAsync(1, "b");
        await tracker.UserConnectedAsync(2, "c");
        await tracker.UserDisconnectedAsync(1, "a");

        await tracker.RefreshAsync();

        var sent = Assert.Single(_store.Refreshes);
        Assert.Equal(new[] { (1, "b"), (2, "c") }, sent.OrderBy(x => x.UserId).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(30), _store.RefreshTtl);
    }

    [Fact]
    public async Task Refresh_DoesNothing_WhenThisServerHoldsNoConnections()
    {
        await Create().RefreshAsync();

        Assert.Empty(_store.Refreshes);
    }

    [Fact]
    public async Task Refresh_SwallowsAStoreFailure_SoTheHeartbeatLoopKeepsRunning()
    {
        var tracker = Create();
        await tracker.UserConnectedAsync(1, "a");
        _store.Fail = true;

        await tracker.RefreshAsync(); // must not throw
    }

    [Fact]
    public async Task ConnectionsMadeDuringARedisOutage_AreRecreatedInRedisByTheNextRefresh()
    {
        var tracker = Create();
        _store.Fail = true;
        await tracker.UserConnectedAsync(5, "during-outage");
        _store.Fail = false;

        await tracker.RefreshAsync();

        Assert.Equal((5, "during-outage"), Assert.Single(Assert.Single(_store.Refreshes)));
    }

    [Fact]
    public void RedisKeys_AreNamedPerUser()
    {
        Assert.Equal("presence:user:42", (string?)RedisPresenceStore.KeyFor(42));
    }

    [Fact]
    public void PresenceTimingDefaults_KeepTheHeartbeatWellInsideTheTtl()
    {
        var defaults = new PresenceOptions();

        Assert.True(defaults.HeartbeatSeconds * 3 <= defaults.TtlSeconds, "a slow beat or short pause must not make a live user look offline");
    }
}

public class InMemoryPresenceTrackerAsyncTests
{
    private readonly InMemoryPresenceTracker _presence = new();

    [Fact]
    public async Task AsyncMethods_BehaveLikeTheSyncOnes()
    {
        Assert.True(await _presence.UserConnectedAsync(7, "t1"));
        Assert.False(await _presence.UserConnectedAsync(7, "t2"));
        Assert.True(await _presence.IsOnlineAsync(7));
        Assert.False(await _presence.UserDisconnectedAsync(7, "t1"));
        Assert.True(await _presence.UserDisconnectedAsync(7, "t2"));
        Assert.False(await _presence.IsOnlineAsync(7));
    }

    [Fact]
    public async Task OnlineAmong_ReturnsOnlyTheOnlineUsers()
    {
        _presence.UserConnected(1, "a");
        _presence.UserConnected(3, "b");

        Assert.Equal(new[] { 1, 3 }, (await _presence.OnlineAmongAsync(new[] { 1, 2, 3 })).OrderBy(x => x).ToArray());
        Assert.Empty(await _presence.OnlineAmongAsync(Array.Empty<int>()));
    }

    [Fact]
    public void Snapshot_ListsEveryConnection()
    {
        _presence.UserConnected(1, "a");
        _presence.UserConnected(1, "b");
        _presence.UserConnected(2, "c");
        _presence.UserDisconnected(1, "a");

        Assert.Equal(new[] { (1, "b"), (2, "c") }, _presence.Snapshot().OrderBy(x => x.UserId).ToArray());
    }

    [Fact]
    public async Task Refresh_IsANoOp()
    {
        await _presence.RefreshAsync();
    }
}

public class RedisConnectionOptionsTests
{
    [Fact]
    public void TheConnectionSettings_BoundAnOutage_ShortTimeouts_ToleratedStartUp_AndAKeepAlivePing()
    {
        var options = RealtimeExtensions.BuildRedisOptions("redis:6379,password=secret");

        Assert.Equal(2000, options.AsyncTimeout);
        Assert.Equal(2000, options.SyncTimeout);
        Assert.Equal(3000, options.ConnectTimeout);
        Assert.False(options.AbortOnConnectFail); // the API starts even when Redis is not up yet
        // A connection whose far end silently stopped answering is noticed after about this many seconds. The library's own
        // default is far longer, which left SignalR pushes dead for a minute after a paused Redis came back.
        Assert.InRange(options.KeepAlive, 1, 10);
        Assert.Equal("aichat-api", options.ClientName);
    }

    [Fact]
    public void TheHostAndPasswordFromTheConnectionString_AreKept()
    {
        var options = RealtimeExtensions.BuildRedisOptions("redis:6379,password=secret");

        Assert.Equal("secret", options.Password);
        Assert.Contains(options.EndPoints, e => e.ToString()!.Contains("redis:6379"));
    }
}

public class RealtimeWiringTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealtime(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithoutRedis_PresenceIsInMemory_AndThereIsNoHeartbeat()
    {
        using var provider = Build(("Redis:ConnectionString", ""));

        Assert.IsType<InMemoryPresenceTracker>(provider.GetRequiredService<IPresenceTracker>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is PresenceHeartbeatService);
    }

    [Fact]
    public void WithRedis_PresenceIsShared_AndTheHeartbeatIsRegistered_WithoutConnectingYet()
    {
        using var provider = Build(("Redis:ConnectionString", "redis:6379,password=secret"));

        Assert.IsType<RedisPresenceTracker>(provider.GetRequiredService<IPresenceTracker>());
        Assert.IsType<RedisPresenceStore>(provider.GetRequiredService<IPresenceStore>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is PresenceHeartbeatService);
    }

    [Fact]
    public void WithRedis_SignalRUsesTheRedisBackplane_AndWithoutRedisItDoesNot()
    {
        var withRedis = new ServiceCollection();
        withRedis.AddLogging();
        withRedis.AddRealtime(new ConfigurationBuilder().AddInMemoutCollectionOf("Redis:ConnectionString", "redis:6379").Build());
        var without = new ServiceCollection();
        without.AddLogging();
        without.AddRealtime(new ConfigurationBuilder().AddInMemoutCollectionOf("Redis:ConnectionString", "").Build());

        static string? Manager(IServiceCollection s) =>
            s.Last(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(HubLifetimeManager<>))
             .ImplementationType?.Name;

        Assert.StartsWith("RedisHubLifetimeManager", Manager(withRedis));
        Assert.StartsWith("DefaultHubLifetimeManager", Manager(without));
    }

    [Fact]
    public void ABadRedisConnectionString_FailsAtStartup_NotOnTheFirstMessage()
    {
        Assert.ThrowsAny<Exception>(() => Build(("Redis:ConnectionString", "redis:6379,notAnOption=1")));
    }
}

internal static class ConfigurationExtensions
{
    public static IConfigurationBuilder AddInMemoutCollectionOf(this IConfigurationBuilder builder, string key, string value) =>
        builder.AddInMemoryCollection(new[] { new KeyValuePair<string, string?>(key, value) });
}
