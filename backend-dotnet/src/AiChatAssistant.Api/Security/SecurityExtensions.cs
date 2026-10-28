using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

namespace AiChatAssistant.Api.Security;

public static class SecurityExtensions
{
    /// <summary>Rate limiting, login lockout and forwarded-headers handling. Counts live in Redis when it is configured, in memory otherwise.</summary>
    public static IServiceCollection AddSecurityHardening(this IServiceCollection services, IConfiguration configuration)
    {
        var rate = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        var lockout = configuration.GetSection(LockoutOptions.SectionName).Get<LockoutOptions>() ?? new LockoutOptions();
        if (rate.Enabled) rate.Validate();
        if (lockout.Enabled) lockout.Validate();
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.Configure<LockoutOptions>(configuration.GetSection(LockoutOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryRateLimitCounter>();
        services.AddSingleton<IRateLimitCounter>(sp =>
        {
            var local = sp.GetRequiredService<InMemoryRateLimitCounter>();
            // IConnectionMultiplexer exists only when Redis is configured (see AddRealtime).
            return sp.GetService<IConnectionMultiplexer>() is { } redis
                ? new ResilientRateLimitCounter(new RedisRateLimitCounter(redis), local, sp.GetRequiredService<ILogger<ResilientRateLimitCounter>>())
                : local;
        });
        services.AddSingleton<ILoginLockout, LoginLockout>();

        var forwarded = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        if (forwarded.Enabled)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // Take the entry the nearest trusted proxy added (the rightmost): anything a client wrote further left is ignored.
                options.ForwardLimit = 1;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var cidr in forwarded.KnownNetworks)
                {
                    var parts = cidr.Split('/');
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : (parts[0].Contains(':') ? 128 : 32)));
                }
                foreach (var proxy in forwarded.KnownProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });
        }

        return services;
    }
}
