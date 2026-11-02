using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Security;
using AiChatAssistant.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    // How long a reset link stays valid before the caller has to request a new one.
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IEmailQueue _emailQueue;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<ForgotPasswordRequest> _forgotPasswordValidator;
    private readonly IValidator<ResetPasswordRequest> _resetPasswordValidator;
    private readonly ILoginLockout _lockout;
    private readonly IValidator<VerifyEmailRequest> _verifyEmailValidator;
    private readonly IValidator<ResendVerificationRequest> _resendVerificationValidator;
    private readonly AccountOptions _account;

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IEmailQueue emailQueue,
        IConfiguration configuration,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator,
        IValidator<ForgotPasswordRequest> forgotPasswordValidator,
        IValidator<ResetPasswordRequest> resetPasswordValidator,
        ILoginLockout lockout,
        IValidator<VerifyEmailRequest> verifyEmailValidator,
        IValidator<ResendVerificationRequest> resendVerificationValidator,
        IOptions<AccountOptions> account,
        ILogger<AuthController> logger)
    {
        _lockout = lockout;
        _verifyEmailValidator = verifyEmailValidator;
        _resendVerificationValidator = resendVerificationValidator;
        _account = account.Value;
        _db = db;
        _tokenService = tokenService;
        _emailQueue = emailQueue;
        _configuration = configuration;
        _registerValidator = registerValidator;
        _loginValidator = loginValidator;
        _forgotPasswordValidator = forgotPasswordValidator;
        _resetPasswordValidator = resetPasswordValidator;
        _logger = logger;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var validation = await _registerValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var existing = await _db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (_account.RequireEmailVerification)
        {
            return await RegisterWithVerificationAsync(request, normalizedEmail, existing, cancellationToken);
        }

        if (existing is not null)
        {
            return Conflict(new ErrorResponse("email_taken", "An account with this email already exists."));
        }

        var userRole = await _db.Roles.SingleAsync(r => r.Name == Role.User, cancellationToken);

        var user = new User
        {
            Name = request.Name.Trim(),
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            // Nothing to confirm while verification is switched off, so turning it on later does not lock these people out.
            EmailVerifiedAt = DateTime.UtcNow,
        };
        user.UserRoles.Add(new UserRole { Role = userRole });

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, BuildAuthResponse(user, new[] { userRole.Name }));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var validation = await _loginValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // Checked before the password, so a locked email is refused even with the right password (otherwise lockout slows nothing down).
        var lockState = await _lockout.CheckAsync(normalizedEmail);
        if (lockState.Locked)
        {
            return AccountLocked(lockState);
        }

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            // Counted for unknown emails exactly as for known ones, so lockout reveals nothing about which emails are registered.
            lockState = await _lockout.RecordFailureAsync(normalizedEmail);
            return lockState.Locked
                ? AccountLocked(lockState)
                : Unauthorized(new ErrorResponse("invalid_credentials", "Email or password is incorrect."));
        }

        await _lockout.ClearAsync(normalizedEmail);

        // After the password check, so this answer is only ever given to someone who knows the password.
        if (_account.RequireEmailVerification && user.EmailVerifiedAt is null)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new ErrorResponse("email_not_verified", "Confirm your email address first. Check your inbox, or request a new link."));
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToArray();
        return Ok(BuildAuthResponse(user, roles));
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<MeResponse> Me()
    {
        var email = User.FindFirstValue(ClaimTypes.Email);
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        return Ok(new MeResponse
        {
            Id = User.GetUserId(),
            Email = email ?? string.Empty,
            Roles = roles,
        });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthMessageResponse>> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var validation = await _forgotPasswordValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        // Same response whether or not the email is registered - a different response here would
        // let a caller enumerate which emails have accounts.
        const string genericMessage = "If that email is registered, a password reset link has been sent.";

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (user is null)
        {
            return Ok(new AuthMessageResponse(genericMessage));
        }

        // Drop any earlier unused links for this user - only the newest one should work.
        var staleTokens = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        _db.PasswordResetTokens.RemoveRange(staleTokens);

        var rawToken = GenerateRawToken();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTime.UtcNow.Add(ResetTokenLifetime),
        });
        await _db.SaveChangesAsync(cancellationToken);

        var frontendBaseUrl = (_configuration["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var resetLink = $"{frontendBaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        try
        {
            await _emailQueue.EnqueuePasswordResetAsync(user.Email, user.Name, resetLink, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A mail or queue failure (SMTP down, bad credentials, not configured, broker unreachable) must not change the answer: a
            // 500 here, against a 200 for an unknown email, would reveal which emails have accounts -
            // the exact thing the generic message exists to prevent. Log it for the operator instead.
            // Logged by user id, not email address.
            _logger.LogError(ex, "Could not send or queue the password reset email for user {UserId}", user.Id);
        }

        return Ok(new AuthMessageResponse(genericMessage));
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthMessageResponse>> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var validation = await _resetPasswordValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var tokenHash = HashToken(request.Token);
        var resetToken = await _db.PasswordResetTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (resetToken is null || resetToken.UsedAt is not null || resetToken.ExpiresAt <= DateTime.UtcNow)
        {
            return BadRequest(new ErrorResponse("invalid_token", "This reset link is invalid or has expired. Request a new one."));
        }

        resetToken.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        resetToken.UsedAt = DateTime.UtcNow;
        // Every older session (a stolen one included) stops working, and reaching this point proves the person owns the mailbox.
        resetToken.User.PasswordChangedAt = resetToken.UsedAt;
        resetToken.User.EmailVerifiedAt ??= resetToken.UsedAt;
        await _db.SaveChangesAsync(cancellationToken);

        // Someone who just proved they own the mailbox must not stay locked out of the account they have just recovered.
        await _lockout.ClearAsync(resetToken.User.Email);

        return Ok(new AuthMessageResponse("Your password has been reset. You can now log in."));
    }

    [HttpPost("verify-email")]
    [AllowAnonymous]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthMessageResponse>> VerifyEmail(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        var validation = await _verifyEmailValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var tokenHash = HashToken(request.Token);
        var token = await _db.EmailVerificationTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (token is null || token.UsedAt is not null || token.ExpiresAt <= DateTime.UtcNow)
        {
            return BadRequest(new ErrorResponse("invalid_token", "This confirmation link is invalid or has expired. Request a new one."));
        }

        token.UsedAt = DateTime.UtcNow;
        token.User.EmailVerifiedAt ??= token.UsedAt;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new AuthMessageResponse("Your email address is confirmed. You can now log in."));
    }

    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [RateLimit(RateLimitPolicy.Auth)]
    public async Task<ActionResult<AuthMessageResponse>> ResendVerification(ResendVerificationRequest request, CancellationToken cancellationToken)
    {
        var validation = await _resendVerificationValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        // The same answer whether or not the address has an account, is confirmed already, or the mail could not be queued.
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (user is not null && user.EmailVerifiedAt is null && _account.RequireEmailVerification)
        {
            await IssueVerificationAsync(user, cancellationToken);
        }

        return Ok(new AuthMessageResponse("If that address needs confirming, a new link has been sent."));
    }

    /// <summary>
    /// Registration while verification is required. A new address gets an account that cannot log in yet and a confirmation mail; an address
    /// that is already unconfirmed gets a fresh mail; an address that is already confirmed gets nothing. All three answer the same 202.
    /// </summary>
    private async Task<ActionResult<AuthResponse>> RegisterWithVerificationAsync(RegisterRequest request, string normalizedEmail, User? existing, CancellationToken cancellationToken)
    {
        // Hashed in every case so the three outcomes take about as long as each other (a bcrypt hash is the slow part of registering).
        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        var user = existing;
        if (user is null)
        {
            var userRole = await _db.Roles.SingleAsync(r => r.Name == Role.User, cancellationToken);
            user = new User { Name = request.Name.Trim(), Email = normalizedEmail, PasswordHash = hash };
            user.UserRoles.Add(new UserRole { Role = userRole });
            _db.Users.Add(user);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Two registrations for one new address at the same instant: the other one won; behave as for an existing address.
                _db.Entry(user).State = EntityState.Detached;
                user = await _db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
            }
        }

        if (user is not null && user.EmailVerifiedAt is null)
        {
            await IssueVerificationAsync(user, cancellationToken);
        }

        return Accepted(new RegistrationPendingResponse());
    }

    /// <summary>Replaces any unused confirmation links with a new one and queues the mail. Never throws: a failure is logged by user id (the answer must not change).</summary>
    private async Task IssueVerificationAsync(User user, CancellationToken cancellationToken)
    {
        var stale = await _db.EmailVerificationTokens.Where(t => t.UserId == user.Id && t.UsedAt == null).ToListAsync(cancellationToken);
        _db.EmailVerificationTokens.RemoveRange(stale);

        var raw = GenerateRawToken();
        _db.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            UserId = user.Id,
            TokenHash = HashToken(raw),
            ExpiresAt = DateTime.UtcNow.AddHours(_account.VerificationLinkHours),
        });
        await _db.SaveChangesAsync(cancellationToken);

        var frontendBaseUrl = (_configuration["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var link = $"{frontendBaseUrl}/verify-email?token={Uri.EscapeDataString(raw)}";
        try
        {
            await _emailQueue.EnqueueEmailVerificationAsync(user.Email, user.Name, link, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not send or queue the email confirmation for user {UserId}", user.Id);
        }
    }

    private ObjectResult AccountLocked(LockoutState state)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(state.RetryAfter.TotalMinutes));
        Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(state.RetryAfter.TotalSeconds)).ToString();
        return StatusCode(
            StatusCodes.Status423Locked,
            new ErrorResponse("account_locked", $"Too many failed login attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}, or reset your password."));
    }

    private static string GenerateRawToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private AuthResponse BuildAuthResponse(User user, IReadOnlyCollection<string> roles)
    {
        var (token, expiresAt) = _tokenService.CreateToken(user, roles);
        return new AuthResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            User = new UserSummary
            {
                Id = user.Id,
                Name = user.Name,
                Email = user.Email,
                Roles = roles,
            },
        };
    }
}
