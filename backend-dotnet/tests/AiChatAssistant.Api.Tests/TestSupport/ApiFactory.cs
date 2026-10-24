using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using Microsoft.AspNetCore.Hosting;
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
        if (AsyncReplies)
        {
            builder.UseSetting("Chat:AsyncReplies", "true");
            builder.UseSetting("RabbitMq:Host", "broker.invalid");
            builder.UseSetting("RabbitMq:Username", "u");
            builder.UseSetting("RabbitMq:Password", "p");
        }

        builder.ConfigureServices(services =>
        {
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

public static class HttpClientExtensions
{
    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
