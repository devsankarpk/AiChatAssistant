using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Account;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Security;
using AiChatAssistant.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Controllers;

/// <summary>The signed-in person's own account: profile, password, and deleting it. Everything here acts on the caller, never on an id from the request.</summary>
[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly ILoginLockout _lockout;
    private readonly IValidator<UpdateProfileRequest> _profileValidator;
    private readonly IValidator<ChangePasswordRequest> _changePasswordValidator;
    private readonly IValidator<DeleteAccountRequest> _deleteValidator;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        AppDbContext db,
        ITokenService tokenService,
        ILoginLockout lockout,
        IValidator<UpdateProfileRequest> profileValidator,
        IValidator<ChangePasswordRequest> changePasswordValidator,
        IValidator<DeleteAccountRequest> deleteValidator,
        ILogger<AccountController> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _lockout = lockout;
        _profileValidator = profileValidator;
        _changePasswordValidator = changePasswordValidator;
        _deleteValidator = deleteValidator;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ProfileResponse>> GetProfile(CancellationToken cancellationToken)
    {
        var user = await LoadCallerAsync(cancellationToken);
        return user is null ? AccountGone() : Ok(ToProfile(user));
    }

    [HttpPut("profile")]
    public async Task<ActionResult<ProfileResponse>> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var validation = await _profileValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var user = await LoadCallerAsync(cancellationToken);
        if (user is null)
        {
            return AccountGone();
        }

        user.Name = request.Name.Trim();
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(ToProfile(user));
    }

    /// <summary>
    /// Needs the current password. Every other session stops working (their tokens predate the change) and this one is given a new token so the
    /// person stays signed in here. Wrong current passwords are counted, and too many lock this action for a while: a stolen session must not
    /// be a way to guess the password.
    /// </summary>
    [HttpPost("change-password")]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var validation = await _changePasswordValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var user = await LoadCallerAsync(cancellationToken, includeRoles: true);
        if (user is null)
        {
            return AccountGone();
        }

        var guardKey = GuardKey(user.Id);
        var locked = await _lockout.CheckAsync(guardKey);
        if (locked.Locked)
        {
            return Locked(locked);
        }

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            locked = await _lockout.RecordFailureAsync(guardKey);
            return locked.Locked
                ? Locked(locked)
                : BadRequest(new ErrorResponse("invalid_current_password", "The current password is not correct."));
        }

        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.PasswordHash))
        {
            return BadRequest(new ErrorResponse("password_unchanged", "Choose a password you are not using now."));
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _lockout.ClearAsync(guardKey);
        await _lockout.ClearAsync(user.Email); // someone who just proved the old password is not a login attacker

        _logger.LogInformation("User {UserId} changed their password; older sessions are now refused", user.Id);
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToArray();
        var (token, expiresAt) = _tokenService.CreateToken(user, roles);
        return Ok(new AuthResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            User = new UserSummary { Id = user.Id, Name = user.Name, Email = user.Email, Roles = roles },
        });
    }

    /// <summary>
    /// Permanently deletes the caller and everything of theirs: chats, messages, usage logs, reset and confirmation tokens, and their direct
    /// conversations with the other person's side of them (the database cascades). Needs the password again. The last Admin cannot delete
    /// themselves, or nobody could ever manage the app again. The token stops working at once (see <see cref="AccountTokenValidator"/>).
    /// </summary>
    [HttpPost("delete")]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<IActionResult> DeleteAccount(DeleteAccountRequest request, CancellationToken cancellationToken)
    {
        var validation = await _deleteValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var user = await LoadCallerAsync(cancellationToken, includeRoles: true);
        if (user is null)
        {
            return AccountGone();
        }

        var guardKey = GuardKey(user.Id);
        var locked = await _lockout.CheckAsync(guardKey);
        if (locked.Locked)
        {
            return Locked(locked);
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            locked = await _lockout.RecordFailureAsync(guardKey);
            return locked.Locked
                ? Locked(locked)
                : BadRequest(new ErrorResponse("invalid_current_password", "The password is not correct."));
        }

        if (user.UserRoles.Any(ur => ur.Role.Name == Role.Admin))
        {
            var admins = await _db.UserRoles.CountAsync(ur => ur.Role.Name == Role.Admin, cancellationToken);
            if (admins <= 1)
            {
                return Conflict(new ErrorResponse("last_admin", "You are the only Admin. Make someone else an Admin before deleting this account."));
            }
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken);
        await _lockout.ClearAsync(guardKey);

        _logger.LogInformation("User {UserId} deleted their account", user.Id);
        return NoContent();
    }

    private async Task<User?> LoadCallerAsync(CancellationToken cancellationToken, bool includeRoles = false)
    {
        IQueryable<User> query = _db.Users;
        if (includeRoles)
        {
            query = query.Include(u => u.UserRoles).ThenInclude(ur => ur.Role);
        }

        return await query.SingleOrDefaultAsync(u => u.Id == User.GetUserId(), cancellationToken);
    }

    private ProfileResponse ToProfile(User user) => new()
    {
        Id = user.Id,
        Name = user.Name,
        Email = user.Email,
        Roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToArray(),
        CreatedAt = user.CreatedAt,
        EmailVerified = user.EmailVerifiedAt is not null,
    };

    // Counted separately from login failures, and under the user id, not the email.
    private static string GuardKey(int userId) => $"account-guard:{userId}";

    private ObjectResult Locked(LockoutState state)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(state.RetryAfter.TotalMinutes));
        Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(state.RetryAfter.TotalSeconds)).ToString();
        return StatusCode(
            StatusCodes.Status423Locked,
            new ErrorResponse("account_locked", $"Too many incorrect passwords. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}."));
    }

    private ObjectResult AccountGone() =>
        StatusCode(StatusCodes.Status401Unauthorized, new ErrorResponse("unauthorized", "Authentication is required."));
}
