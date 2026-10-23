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

        var redis = ConfigurationOptions.Parse(redisConnection);
        // Start even when Redis is not up yet and keep trying: a Redis outage must not stop the API booting.
        redis.AbortOnConnectFail = false;
        redis.ClientName = "aichat-api";
        // A down Redis must slow a request by seconds at most, not the library's default of 5 s per call.
        redis.AsyncTimeout = 2000;
        redis.SyncTimeout = 2000;
        redis.ConnectTimeout = 3000;

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
        return services;
    }
}
