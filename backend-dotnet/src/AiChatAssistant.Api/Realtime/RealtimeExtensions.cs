using AiChatAssistant.Api.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace AiChatAssistant.Api.Realtime;

public static class RealtimeExtensions
{
    /// <summary>
    /// SignalR plus presence. With Redis:ConnectionString set, SignalR uses the Redis backplane (a push from
    /// one API replica reaches users connected to another) and presence lives in Redis, so the API can run as
    /// several replicas. Without it, everything stays in this process, which is correct for exactly one replica.
    /// </summary>
    public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConnection = configuration["Redis:ConnectionString"];
        var signalR = services.AddSignalR();
        services.TryAddSingleton(TimeProvider.System);

        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
            return services;
        }

        var redis = BuildRedisOptions(redisConnection);

        signalR.AddStackExchangeRedis(options =>
        {
            options.Configuration = redis.Clone();
            // Namespaces our channels in case the Redis instance is shared with something else.
            options.Configuration.ChannelPrefix = RedisChannel.Literal("aichat");
        });

        services.Configure<PresenceOptions>(configuration.GetSection(PresenceOptions.SectionName));
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        services.AddSingleton<IPresenceStore, RedisPresenceStore>();
        services.AddSingleton<IPresenceTracker, RedisPresenceTracker>();
        services.AddHostedService<PresenceHeartbeatService>();
        services.AddHostedService<PresenceSweeper>();
        return services;
    }

    /// <summary>
    /// How this app connects to Redis. Separate and public so a test can check the settings that matter in an outage:
    /// short command timeouts (a down Redis slows a request by seconds, not the library's 5 s), start-up that tolerates
    /// Redis not being there yet, and a keep-alive ping every few seconds. Without the keep-alive, a connection whose far
    /// end silently stopped answering (a paused or partitioned Redis) is only noticed after the library's default of about
    /// a minute, so SignalR pushes stayed dead for that long after Redis came back (seen in the black-hole test).
    /// </summary>
    public static ConfigurationOptions BuildRedisOptions(string connectionString)
    {
        var redis = ConfigurationOptions.Parse(connectionString);
        redis.AbortOnConnectFail = false;
        redis.ClientName = "aichat-api";
        redis.AsyncTimeout = 2000;
        redis.SyncTimeout = 2000;
        redis.ConnectTimeout = 3000;
        redis.KeepAlive = 4;
        return redis;
    }
}
