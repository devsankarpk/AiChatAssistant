using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Tests.TestSupport;

public static class TestDbContextFactory
{
    /// <summary>A fresh, isolated in-memory AppDbContext, seeded (via EnsureCreated) the same way
    /// the real InitialCreate migration seeds it - Admin/User roles.</summary>
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
