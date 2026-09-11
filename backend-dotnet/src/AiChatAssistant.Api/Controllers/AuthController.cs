using System.Security.Claims;
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
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator)
    {
        _db = db;
        _tokenService = tokenService;
        _registerValidator = registerValidator;
        _loginValidator = loginValidator;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var validation = await _registerValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationError(validation);
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
            return ValidationError(validation);
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
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var email = User.FindFirstValue(ClaimTypes.Email);
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        return Ok(new MeResponse
        {
            Id = int.TryParse(subject, out var id) ? id : 0,
            Email = email ?? string.Empty,
            Roles = roles,
        });
    }

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

    private ActionResult ValidationError(FluentValidation.Results.ValidationResult validation)
    {
        var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
        return BadRequest(new ErrorResponse("validation_error", message));
    }
}
