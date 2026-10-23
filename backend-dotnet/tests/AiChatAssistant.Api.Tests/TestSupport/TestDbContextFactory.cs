using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiChatAssistant.Api.Tests.TestSupport;

public static class TestDbContextFactory
{
    /// <summary>A fresh, isolated in-memory AppDbContext, seeded (via EnsureCreated) the same way
    /// the real InitialCreate migration seeds it - Admin/User roles.</summary>
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The outbox saves a change and its events in one transaction; the in-memory store has no transactions, so it ignores the request.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
