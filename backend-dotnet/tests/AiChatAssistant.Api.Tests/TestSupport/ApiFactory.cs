using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace AiChatAssistant.Api.Tests.TestSupport;

/// <summary>
/// The whole API pipeline in memory (routing, JWT validation, authorization, error shapes, controllers, SignalR negotiate)
/// with the real <c>Program.cs</c>. Only the edges are replaced: the database (EF InMemory), the AI client (a Moq mock) and
/// the email queue. No MySQL, Redis, RabbitMQ, SMTP or LLM is ever contacted.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-signing-key-test-signing-key-0123456789";
    public const string InternalKey = "test-internal-key";

    public Mock<IPythonAiClient> Python { get; } = new();
    public Mock<IEmailQueue> Email { get; } = new();
    public Mock<IAiReplyQueue> ReplyQueue { get; } = new();

    /// <summary>Switch on the async reply path (needs a RabbitMQ host to pass start-up validation; the broker itself is replaced).</summary>
    public bool AsyncReplies { get; init; }

    /// <summary>Extra configuration for a test class that needs, for example, a low rate limit. Applied after the defaults below.</summary>
    public Dictionary<string, string> Settings { get; } = new();

    /// <summary>The address the test server pretends the caller connected from, when a test sends no X-Test-Remote-Ip header (a trusted private address, like the proxy in the containers).</summary>
    public const string DefaultRemoteIp = "10.0.0.9";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot _root = new();

    static ApiFactory()
    {
        // Program.cs reads these while it builds, before a test host could add its own configuration, so they go in the environment.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", "Server=unused;Database=unused;User=unused;Password=unused");
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("PythonService__InternalApiKey", InternalKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // The abuse limits are off by default so ordinary tests can make as many requests as they like; the tests that are about the limits turn them on.
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.UseSetting("RateLimiting:ChatPerMinute", "100000");
        builder.UseSetting("Lockout:MaxFailedAttempts", "100000");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
        if (AsyncReplies)
        {
            builder.UseSetting("Chat:AsyncReplies", "true");
            builder.UseSetting("RabbitMq:Host", "broker.invalid");
            builder.UseSetting("RabbitMq:Username", "u");
            builder.UseSetting("RabbitMq:Password", "p");
        }

        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, FakeRemoteIpFilter>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(_dbName, _root)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning)));

            services.RemoveAll<IPythonAiClient>();
            services.AddSingleton(Python.Object);
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton(Email.Object);

            if (AsyncReplies)
            {
                services.RemoveAll<IAiReplyQueue>();
                services.AddSingleton(ReplyQueue.Object);
                // No broker is running: drop every background worker that would try to connect to one.
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(d);
                }
            }
        });
    }

    public T WithDb<T>(Func<AppDbContext, T> use)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        return use(db);
    }

    /// <summary>Adds a user with a role and returns a real signed JWT for them, made by the real <see cref="TokenService"/>.</summary>
    public (User User, string Token) CreateUser(string name, string email, params string[] roles)
    {
        return WithDb(db =>
        {
            var user = new User { Name = name, Email = email, PasswordHash = "x" };
            db.Users.Add(user);
            db.SaveChanges();
            var roleRows = db.Roles.Where(r => roles.Contains(r.Name)).ToList();
            foreach (var role in roleRows)
            {
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            }
            db.SaveChanges();

            using var scope = Services.CreateScope();
            var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateToken(user, roles).Token;
            return (user, token);
        });
    }
}

/// <summary>
/// The test server has no network, so every request would arrive from "no address". This gives each one a caller address (the
/// X-Test-Remote-Ip header, else a trusted private one) before anything else runs, which is what the forwarded-headers and rate-limit tests need.
/// </summary>
internal sealed class FakeRemoteIpFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var fake = context.Request.Headers["X-Test-Remote-Ip"].FirstOrDefault() ?? ApiFactory.DefaultRemoteIp;
            context.Connection.RemoteIpAddress = IPAddress.Parse(fake);
            await nextMiddleware();
        });
        next(app);
    };
}

public static class HttpClientExtensions
{
    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
