using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    // How long a reset link stays valid before the caller has to request a new one.
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IEmailSender _emailSender;
    private readonly IConfiguration _configuration;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<ForgotPasswordRequest> _forgotPasswordValidator;
    private readonly IValidator<ResetPasswordRequest> _resetPasswordValidator;

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IEmailSender emailSender,
        IConfiguration configuration,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator,
        IValidator<ForgotPasswordRequest> forgotPasswordValidator,
        IValidator<ResetPasswordRequest> resetPasswordValidator)
    {
        _db = db;
        _tokenService = tokenService;
        _emailSender = emailSender;
        _configuration = configuration;
        _registerValidator = registerValidator;
        _loginValidator = loginValidator;
        _forgotPasswordValidator = forgotPasswordValidator;
        _resetPasswordValidator = resetPasswordValidator;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var validation = await _registerValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var emailTaken = await _db.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (emailTaken)
        {
            return Conflict(new ErrorResponse("email_taken", "An account with this email already exists."));
        }

        var userRole = await _db.Roles.SingleAsync(r => r.Name == Role.User, cancellationToken);

        var user = new User
        {
            Name = request.Name.Trim(),
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        };
        user.UserRoles.Add(new UserRole { Role = userRole });

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, BuildAuthResponse(user, new[] { userRole.Name }));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var validation = await _loginValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new ErrorResponse("invalid_credentials", "Email or password is incorrect."));
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
        await _emailSender.SendPasswordResetEmailAsync(user.Email, user.Name, resetLink, cancellationToken);

        return Ok(new AuthMessageResponse(genericMessage));
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
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
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new AuthMessageResponse("Your password has been reset. You can now log in."));
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
