using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiChatAssistant.Api.Controllers;

/// <summary>
/// Placeholder for the Admin-only surface (usage logs, etc. arrive in a later phase).
/// Exists now to prove role-based authorization end to end.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    [HttpGet("ping")]
    public ActionResult<object> Ping() => Ok(new { message = "pong", scope = "admin-only" });
}
