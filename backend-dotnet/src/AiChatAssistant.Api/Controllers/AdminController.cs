using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;

    public AdminController(AppDbContext db)
    {
        _db = db;
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

    private static UsageLogResponse ToUsageLogResponse(Models.UsageLog u) => new()
    {
        Id = u.Id,
        UserId = u.UserId,
        UserName = u.User.Name,
        UserEmail = u.User.Email,
        SessionId = u.SessionId,
        SessionTitle = u.Session.Title,
        TokensUsed = u.TokensUsed,
        CostEstimate = u.CostEstimate,
        CreatedAt = u.CreatedAt,
    };
}
