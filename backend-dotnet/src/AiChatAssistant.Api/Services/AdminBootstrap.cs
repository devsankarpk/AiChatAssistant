using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

/// <summary>
/// The way the first Admin is made without touching the database by hand: set <c>Admin:BootstrapEmail</c> to the address of the account
/// that should be the first administrator. It is promoted when that account exists AND nobody holds the Admin role. The second condition
/// matters: once there is an administrator the setting does nothing, so a demoted administrator is not quietly promoted again at the next
/// restart, and a later change of the setting cannot be used to take over an installation that already has one.
/// </summary>
public static class AdminBootstrap
{
    public const string SettingName = "Admin:BootstrapEmail";

    /// <returns>true when the account was promoted by this call.</returns>
    public static async Task<bool> TryPromoteAsync(AppDbContext db, string? configuredEmail, string? emailToCheck, CancellationToken cancellationToken)
    {
        var configured = configuredEmail?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(configured))
        {
            return false;
        }
        if (emailToCheck is not null && !string.Equals(emailToCheck.Trim(), configured, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var admin = await db.Roles.SingleOrDefaultAsync(r => r.Name == Role.Admin, cancellationToken);
        if (admin is null || await db.UserRoles.AnyAsync(ur => ur.RoleId == admin.Id, cancellationToken))
        {
            return false;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == configured, cancellationToken);
        if (user is null)
        {
            return false;
        }

        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = admin.Id });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Runs <see cref="AdminBootstrap"/> once at start-up, for an account that already exists. A failure is logged, never fatal.</summary>
public sealed class AdminBootstrapService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminBootstrapService> _logger;

    public AdminBootstrapService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AdminBootstrapService> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = _configuration[AdminBootstrap.SettingName];
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await AdminBootstrap.TryPromoteAsync(db, email, null, cancellationToken))
            {
                _logger.LogWarning("{Setting} is set and the installation had no administrator: the account was made an Admin", AdminBootstrap.SettingName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not apply {Setting} at start-up", AdminBootstrap.SettingName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
