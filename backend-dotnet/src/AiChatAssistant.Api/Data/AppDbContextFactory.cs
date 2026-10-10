using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiChatAssistant.Api.Data;

/// <summary>
/// Used only by EF Core's tooling (dotnet ef, migration bundles) - never at app runtime. Without
/// it, EF builds the whole app host to find the DbContext, and Program.cs's
/// ServerVersion.AutoDetect then needs a live MySQL, which doesn't exist while building a
/// container image. The fixed server version only affects the SQL the tooling generates.
/// Connection string: appsettings.json / appsettings.{env}.json, overridden by the
/// ConnectionStrings__Default environment variable (what the migrate container sets).
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Default")
            ?? "Server=design-time-placeholder;Database=AiChatAssistant;User=x;Password=x";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;

        return new AppDbContext(options);
    }
}
