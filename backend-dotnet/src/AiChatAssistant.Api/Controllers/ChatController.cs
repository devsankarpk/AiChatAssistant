using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    // How many prior messages to send Python as context on each turn. Python is stateless and
    // has no DB access (see CLAUDE.md) - .NET always sends the full window itself.
    private const int HistoryLimit = 20;

    private readonly AppDbContext _db;
    private readonly IPythonAiClient _pythonClient;
    private readonly IValidator<CreateSessionRequest> _createSessionValidator;
    private readonly IValidator<SendMessageRequest> _sendMessageValidator;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        AppDbContext db,
        IPythonAiClient pythonClient,
        IValidator<CreateSessionRequest> createSessionValidator,
        IValidator<SendMessageRequest> sendMessageValidator,
        ILogger<ChatController> logger)
    {
        _db = db;
        _pythonClient = pythonClient;
        _createSessionValidator = createSessionValidator;
        _sendMessageValidator = sendMessageValidator;
        _logger = logger;
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<SessionResponse>> CreateSession(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        var validation = await _createSessionValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = User.GetUserId(),
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New chat" : request.Title.Trim(),
        };
        _db.ChatSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToSessionResponse(session));
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> ListSessions(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var sessions = await _db.ChatSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(sessions.Select(ToSessionResponse).ToList());
    }

    [HttpGet("sessions/{id:guid}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageResponse>>> GetMessages(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(id, cancellationToken);
        if (session is null)
        {
            return SessionNotFound();
        }

        var messages = await _db.Messages
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);

        return Ok(messages.Select(ToMessageResponse).ToList());
    }

    [HttpPost("sessions/{id:guid}/messages")]
    public async Task<ActionResult<SendMessageResponse>> SendMessage(Guid id, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var validation = await _sendMessageValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var session = await GetOwnedSessionAsync(id, cancellationToken);
        if (session is null)
        {
            return SessionNotFound();
        }

        var userMessage = new Message
        {
            SessionId = id,
            Role = MessageRole.User,
            Content = request.Content.Trim(),
        };
        _db.Messages.Add(userMessage);
        // Saved on its own, before the Python call - so a Python failure below still leaves this
        // persisted, per CLAUDE.md ("... the user message stays persisted").
        await _db.SaveChangesAsync(cancellationToken);

        var priorMessages = await _db.Messages
            .Where(m => m.SessionId == id && m.Id < userMessage.Id)
            .OrderByDescending(m => m.Id)
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);
        priorMessages.Reverse(); // chronological order (oldest first) for the LLM

        var history = priorMessages
            .Select(m => new PythonHistoryMessage(m.Role.ToString().ToLowerInvariant(), m.Content))
            .ToList();

        PythonGenerateResult result;
        try
        {
            result = await _pythonClient.GenerateAsync(id, history, userMessage.Content, cancellationToken);
        }
        catch (PythonServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "AI service unavailable for session {SessionId}", id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse(
                    "ai_service_unavailable",
                    "The AI service is temporarily unavailable. Your message was saved - try sending again shortly."));
        }

        var assistantMessage = new Message
        {
            SessionId = id,
            Role = MessageRole.Assistant,
            Content = result.Reply,
        };
        _db.Messages.Add(assistantMessage);
        _db.UsageLogs.Add(new UsageLog
        {
            UserId = User.GetUserId(),
            SessionId = id,
            TokensUsed = result.TokensUsed,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new SendMessageResponse
        {
            UserMessage = ToMessageResponse(userMessage),
            AssistantMessage = ToMessageResponse(assistantMessage),
        });
    }

    /// <summary>Null unless the session exists AND (the caller owns it OR the caller is Admin).</summary>
    private async Task<ChatSession?> GetOwnedSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        return session.UserId == User.GetUserId() || User.IsAdmin() ? session : null;
    }

    // 404, not 403, for "not yours" too - avoids confirming to a caller that a given session id
    // exists at all when it isn't theirs.
    private ActionResult SessionNotFound() => NotFound(new ErrorResponse("session_not_found", "Chat session not found."));

    private static SessionResponse ToSessionResponse(ChatSession s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        CreatedAt = s.CreatedAt,
    };

    private static MessageResponse ToMessageResponse(Message m) => new()
    {
        Id = m.Id,
        Role = m.Role.ToString().ToLowerInvariant(),
        Content = m.Content,
        CreatedAt = m.CreatedAt,
    };
}
