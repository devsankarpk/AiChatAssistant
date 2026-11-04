using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private const int MaxExportRows = 50_000;
    private const int TopUsers = 50;
    private const int MaxDays = 366;

    private readonly AppDbContext _db;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext db, IRefreshTokenService refreshTokens, ILogger<AdminController>? logger = null)
    {
        _db = db;
        _refreshTokens = refreshTokens;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminController>.Instance;
    }

    /// <summary>Kept from Phase 2 - a trivial endpoint proving role-based authorization works end to end.</summary>
    [HttpGet("ping")]
    public ActionResult<object> Ping() => Ok(new { message = "pong", scope = "admin-only" });

    [HttpGet("usage")]
    public async Task<ActionResult<PagedUsageLogResponse>> GetUsage(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default)
    {
        if (from is not null && to is not null && from > to)
        {
            return BadRequest(new ErrorResponse("validation_error", "'from' must not be after 'to'."));
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.UsageLogs.AsQueryable();
        if (from is not null)
        {
            query = query.Where(u => u.CreatedAt >= from.Value);
        }
        if (to is not null)
        {
            query = query.Where(u => u.CreatedAt <= to.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var pageItems = await query
            .Include(u => u.User)
            .Include(u => u.Session)
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new PagedUsageLogResponse
        {
            Items = pageItems.Select(ToUsageLogResponse).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        });
    }

    /// <summary>Totals, the biggest spenders and the per-day series over the whole filtered range, computed by the database.</summary>
    [HttpGet("usage/summary")]
    public async Task<ActionResult<UsageSummaryResponse>> GetUsageSummary(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, CancellationToken cancellationToken = default)
    {
        if (from is not null && to is not null && from > to)
        {
            return BadRequest(new ErrorResponse("validation_error", "'from' must not be after 'to'."));
        }

        var query = FilterUsage(from, to);

        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new UsageTotals
            {
                Requests = g.Count(),
                Tokens = g.Sum(u => (long)u.TokensUsed),
                PromptTokens = g.Sum(u => (long)u.PromptTokens),
                CompletionTokens = g.Sum(u => (long)u.CompletionTokens),
                Cost = g.Sum(u => u.CostEstimate),
            })
            .FirstOrDefaultAsync(cancellationToken) ?? new UsageTotals();

        var perUser = await query
            .GroupBy(u => u.UserId)
            .Select(g => new { UserId = g.Key, Requests = g.Count(), Tokens = g.Sum(u => (long)u.TokensUsed), Cost = g.Sum(u => u.CostEstimate) })
            .OrderByDescending(x => x.Cost).ThenByDescending(x => x.Tokens)
            .Take(TopUsers)
            .ToListAsync(cancellationToken);
        var ids = perUser.Select(x => x.UserId).ToList();
        var people = await _db.Users.Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Name, u.Email }).ToDictionaryAsync(u => u.Id, cancellationToken);

        var perDay = await query
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Requests = g.Count(), Tokens = g.Sum(u => (long)u.TokensUsed), Cost = g.Sum(u => u.CostEstimate) })
            .OrderByDescending(x => x.Day)
            .Take(MaxDays)
            .ToListAsync(cancellationToken);

        return Ok(new UsageSummaryResponse
        {
            Totals = totals,
            ByUser = perUser.Select(x => new UsageByUser
            {
                UserId = x.UserId,
                UserName = people.TryGetValue(x.UserId, out var p) ? p.Name : string.Empty,
                UserEmail = p?.Email ?? string.Empty,
                Requests = x.Requests,
                Tokens = x.Tokens,
                Cost = x.Cost,
            }).ToList(),
            ByDay = perDay.OrderBy(x => x.Day).Select(x => new UsageByDay
            {
                Date = x.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Requests = x.Requests,
                Tokens = x.Tokens,
                Cost = x.Cost,
            }).ToList(),
        });
    }

    /// <summary>The filtered usage rows as a CSV file, newest first, at most 50,000 rows. Cells that a spreadsheet would run as a formula are defused.</summary>
    [HttpGet("usage/export")]
    public async Task<IActionResult> ExportUsage(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, CancellationToken cancellationToken = default)
    {
        if (from is not null && to is not null && from > to)
        {
            return BadRequest(new ErrorResponse("validation_error", "'from' must not be after 'to'."));
        }

        var rows = await FilterUsage(from, to)
            .Include(u => u.User).Include(u => u.Session)
            .OrderByDescending(u => u.CreatedAt)
            .Take(MaxExportRows)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.Append("Time (UTC),User,Email,Chat,Model,Prompt tokens,Completion tokens,Total tokens,Cost (USD)\r\n");
        foreach (var u in rows)
        {
            csv.Append(string.Join(',',
                    CsvCell(u.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    CsvCell(u.User.Name),
                    CsvCell(u.User.Email),
                    CsvCell(u.Session.Title),
                    CsvCell(u.Model),
                    u.PromptTokens.ToString(CultureInfo.InvariantCulture),
                    u.CompletionTokens.ToString(CultureInfo.InvariantCulture),
                    u.TokensUsed.ToString(CultureInfo.InvariantCulture),
                    u.CostEstimate.ToString("0.########", CultureInfo.InvariantCulture)))
                .Append("\r\n");
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"usage-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    // ---- users ----

    [HttpGet("users")]
    public async Task<ActionResult<PagedAdminUsersResponse>> GetUsers(
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.Users.AsQueryable();
        var term = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
        {
            term = term.Length > 100 ? term[..100] : term;
            query = query.Where(u => u.Name.ToLower().Contains(term) || u.Email.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Name).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new PagedAdminUsersResponse
        {
            Items = items.Select(ToAdminUser).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize),
        });
    }

    /// <summary>
    /// Makes the person an Admin or an ordinary User (exactly one of the two). Not for yourself, and never for the last active Admin.
    /// Roles travel inside the token, so the person's current sessions are ended and the new role applies when they log in again.
    /// </summary>
    [HttpPut("users/{id:int}/role")]
    public async Task<ActionResult<AdminUserResponse>> ChangeRole(int id, ChangeRoleRequest request, CancellationToken cancellationToken)
    {
        if (request.Role is not (Role.Admin or Role.User))
        {
            return BadRequest(new ErrorResponse("validation_error", "Role must be 'Admin' or 'User'."));
        }
        var user = await LoadUserAsync(id, cancellationToken);
        if (user is null)
        {
            return UserNotFound();
        }
        if (id == User.GetUserId())
        {
            return Conflict(new ErrorResponse("cannot_change_own_role", "You cannot change your own role. Ask another administrator."));
        }

        var isAdmin = user.UserRoles.Any(ur => ur.Role.Name == Role.Admin);
        if (request.Role == Role.User && isAdmin && !await AnotherActiveAdminExistsAsync(id, cancellationToken))
        {
            return Conflict(new ErrorResponse("last_admin", "This is the last active administrator. Make someone else an Admin first."));
        }
        if ((request.Role == Role.Admin) == isAdmin)
        {
            return Ok(ToAdminUser(user)); // already what was asked for
        }

        var wanted = await _db.Roles.SingleAsync(r => r.Name == request.Role, cancellationToken);
        _db.UserRoles.RemoveRange(user.UserRoles);
        user.UserRoles.Clear();
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = wanted.Id, Role = wanted });
        await EndSessionsAsync(user, cancellationToken);
        _logger.LogWarning("Administrator {AdminId} changed the role of user {UserId} to {Role}", User.GetUserId(), id, request.Role);
        return Ok(ToAdminUser(user));
    }

    /// <summary>Disables an account: no login, no renewing a session, existing tokens refused at once. Nothing is deleted. Not for yourself or the last active Admin.</summary>
    [HttpPost("users/{id:int}/disable")]
    public async Task<ActionResult<AdminUserResponse>> DisableUser(int id, CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(id, cancellationToken);
        if (user is null)
        {
            return UserNotFound();
        }
        if (id == User.GetUserId())
        {
            return Conflict(new ErrorResponse("cannot_disable_self", "You cannot disable your own account."));
        }
        if (user.DisabledAt is not null)
        {
            return Ok(ToAdminUser(user));
        }
        if (user.UserRoles.Any(ur => ur.Role.Name == Role.Admin) && !await AnotherActiveAdminExistsAsync(id, cancellationToken))
        {
            return Conflict(new ErrorResponse("last_admin", "This is the last active administrator. Make someone else an Admin first."));
        }

        user.DisabledAt = DateTime.UtcNow;
        await EndSessionsAsync(user, cancellationToken);
        _logger.LogWarning("Administrator {AdminId} disabled user {UserId}", User.GetUserId(), id);
        return Ok(ToAdminUser(user));
    }

    [HttpPost("users/{id:int}/enable")]
    public async Task<ActionResult<AdminUserResponse>> EnableUser(int id, CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(id, cancellationToken);
        if (user is null)
        {
            return UserNotFound();
        }
        if (user.DisabledAt is not null)
        {
            user.DisabledAt = null;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Administrator {AdminId} enabled user {UserId}", User.GetUserId(), id);
        }
        return Ok(ToAdminUser(user));
    }

    private IQueryable<UsageLog> FilterUsage(DateTime? from, DateTime? to)
    {
        var query = _db.UsageLogs.AsQueryable();
        if (from is not null)
        {
            query = query.Where(u => u.CreatedAt >= from.Value);
        }
        if (to is not null)
        {
            query = query.Where(u => u.CreatedAt <= to.Value);
        }
        return query;
    }

    private Task<User?> LoadUserAsync(int id, CancellationToken cancellationToken) =>
        _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    private Task<bool> AnotherActiveAdminExistsAsync(int exceptUserId, CancellationToken cancellationToken) =>
        _db.UserRoles.AnyAsync(ur => ur.Role.Name == Role.Admin && ur.UserId != exceptUserId && ur.User.DisabledAt == null, cancellationToken);

    /// <summary>Ends every session of the person: older tokens are refused (the stamp) and their refresh tokens stop working. Saves.</summary>
    private async Task EndSessionsAsync(User user, CancellationToken cancellationToken)
    {
        user.PasswordChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _refreshTokens.RevokeAllAsync(user.Id, cancellationToken);
    }

    private ActionResult UserNotFound() => NotFound(new ErrorResponse("user_not_found", "User not found."));

    private static AdminUserResponse ToAdminUser(User u) => new()
    {
        Id = u.Id,
        Name = u.Name,
        Email = u.Email,
        Roles = u.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToList(),
        CreatedAt = u.CreatedAt,
        LastSeenAt = u.LastSeenAt,
        DisabledAt = u.DisabledAt,
    };

    /// <summary>Quotes a CSV cell, and defuses text a spreadsheet would run as a formula (leading = + - @, tab or carriage return).</summary>
    private static string CsvCell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        {
            value = "'" + value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static UsageLogResponse ToUsageLogResponse(Models.UsageLog u) => new()
    {
        Id = u.Id,
        UserId = u.UserId,
        UserName = u.User.Name,
        UserEmail = u.User.Email,
        SessionId = u.SessionId,
        SessionTitle = u.Session.Title,
        TokensUsed = u.TokensUsed,
        PromptTokens = u.PromptTokens,
        CompletionTokens = u.CompletionTokens,
        Model = u.Model,
        CostEstimate = u.CostEstimate,
        CreatedAt = u.CreatedAt,
    };
}
