using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Chat;
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
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPythonAiClient _pythonClient;
    private readonly IValidator<CreateSessionRequest> _createSessionValidator;
    private readonly IValidator<SendMessageRequest> _sendMessageValidator;
    private readonly IValidator<RenameSessionRequest> _renameValidator;
    private readonly ILogger<ChatController> _logger;
    private readonly IAiReplyQueue? _replyQueue;
    private readonly ChatOptions _chatOptions;

    public ChatController(
        AppDbContext db,
        IPythonAiClient pythonClient,
        IValidator<CreateSessionRequest> createSessionValidator,
        IValidator<SendMessageRequest> sendMessageValidator,
        IValidator<RenameSessionRequest> renameValidator,
        ILogger<ChatController> logger,
        IOptions<ChatOptions> chatOptions,
        IAiReplyQueue? replyQueue = null)
    {
        _chatOptions = chatOptions.Value;
        _replyQueue = replyQueue;
        _db = db;
        _pythonClient = pythonClient;
        _createSessionValidator = createSessionValidator;
        _sendMessageValidator = sendMessageValidator;
        _renameValidator = renameValidator;
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
            Title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle : request.Title.Trim(),
        };
        _db.ChatSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToSessionResponse(session));
    }

    /// <summary>
    /// The caller's chats, newest first, deleted ones left out. <c>search</c> keeps chats whose title contains the text;
    /// <c>limit</c> (default 50, max 100) and <c>before</c> (the <c>createdAt</c> of the last chat received) page through them.
    /// The answer stays a plain array, so a client that sends no parameters still works, it just sees the newest 50.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> ListSessions(
        string? search = null, DateTime? before = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var query = _db.ChatSessions.Where(s => s.UserId == userId && s.DeletedAt == null);

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > SearchMaxLength)
            {
                term = term[..SearchMaxLength];
            }
            var lowered = term.ToLower();
            query = query.Where(s => s.Title.ToLower().Contains(lowered));
        }

        if (before is not null)
        {
            var cursor = before.Value.Kind == DateTimeKind.Local ? before.Value.ToUniversalTime() : before.Value;
            query = query.Where(s => s.CreatedAt < cursor);
        }

        var sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .Take(PageSize(limit))
            .ToListAsync(cancellationToken);

        return Ok(sessions.Select(ToSessionResponse).ToList());
    }

    /// <summary>Renames a chat. Title is trimmed, 1 to 200 characters. 404 for a chat that is not the caller's or is deleted.</summary>
    [HttpPatch("sessions/{id:guid}")]
    public async Task<ActionResult<SessionResponse>> RenameSession(Guid id, RenameSessionRequest request, CancellationToken cancellationToken)
    {
        var validation = await _renameValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var session = await GetOwnedSessionAsync(id, cancellationToken);
        if (session is null)
        {
            return SessionNotFound();
        }

        session.Title = request.Title.Trim();
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(ToSessionResponse(session));
    }

    /// <summary>
    /// Deletes a chat for its owner. A soft delete: the row and its messages stay in the database so the usage history
    /// that points at them is kept, but the chat disappears from the list and every endpoint answers 404 for it.
    /// A second delete is a 404 too, because the chat no longer exists as far as the caller can tell.
    /// </summary>
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(id, cancellationToken);
        if (session is null)
        {
            return SessionNotFound();
        }

        session.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The messages of a chat, oldest first. With no parameters: the newest <c>limit</c> (default 50, max 100).
    /// <c>before</c> (a message id) gives the page of messages older than that one, so a client scrolls upwards.
    /// </summary>
    [HttpGet("sessions/{id:guid}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageResponse>>> GetMessages(Guid id, int? before = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var session = await GetOwnedSessionAsync(id, cancellationToken);
        if (session is null)
        {
            return SessionNotFound();
        }

        var query = _db.Messages.Where(m => m.SessionId == id);
        if (before is not null)
        {
            query = query.Where(m => m.Id < before.Value);
        }

        var page = await query
            .OrderByDescending(m => m.Id)
            .Take(PageSize(limit))
            .ToListAsync(cancellationToken);
        page.Reverse(); // oldest first within the page

        return Ok(page.Select(ToMessageResponse).ToList());
    }

    [HttpPost("sessions/{id:guid}/messages")]
    [RateLimit(RateLimitPolicy.Chat)]
    [ProducesResponseType<SendMessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<SendMessageAcceptedResponse>(StatusCodes.Status202Accepted)]
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
        if (session.Title == DefaultTitle)
        {
            session.Title = TitleFromMessage(userMessage.Content);
        }
        // Async mode: the job row is saved in the same transaction as the question, so there is never a question
        // that was accepted but has no job, nor a job without its question.
        AiReplyJob? job = null;
        if (_chatOptions.AsyncReplies && _replyQueue is not null)
        {
            job = new AiReplyJob
            {
                Id = Guid.NewGuid(),
                SessionId = id,
                UserId = User.GetUserId(),
                UserMessage = userMessage,
                ClaimToken = Guid.NewGuid(),
            };
            _db.AiReplyJobs.Add(job);
        }

        // Saved on its own, before the Python call - so a Python failure below still leaves this
        // (and the title) persisted, per CLAUDE.md ("... the user message stays persisted").
        await _db.SaveChangesAsync(cancellationToken);

        if (job is not null)
        {
            try
            {
                await _replyQueue!.EnqueueAsync(job.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The broker did not take the job. The question stays saved (as with an AI failure in the
                // synchronous path); the orphan job is removed so nothing waits for a reply that will never come.
                _logger.LogWarning(ex, "Could not queue the AI reply job {JobId} for session {SessionId}", job.Id, id);
                _db.AiReplyJobs.Remove(job);
                await _db.SaveChangesAsync(CancellationToken.None);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new ErrorResponse(
                        "ai_service_unavailable",
                        "The AI service is temporarily unavailable. Your message was saved - try sending again shortly."));
            }

            return Accepted(new SendMessageAcceptedResponse
            {
                UserMessage = ToMessageResponse(userMessage),
                ReplyStatus = "pending",
                JobId = job.Id,
            });
        }

        var history = await ChatHistory.LoadAsync(_db, id, userMessage.Id, cancellationToken);

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

    /// <summary>
    /// Replies that are not delivered yet for this chat: still being produced ("pending") or given up on ("failed").
    /// A page reload uses this to show "thinking" or a failed state for an async reply.
    /// </summary>
    [HttpGet("sessions/{id:guid}/jobs")]
    public async Task<ActionResult<IReadOnlyList<ReplyJobResponse>>> GetOpenReplyJobs(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(id, cancellationToken);
        if (session is null)
        {
            return SessionNotFound();
        }

        var jobs = await _db.AiReplyJobs
            .Where(j => j.SessionId == id && j.Status != AiReplyJobStatus.Done)
            .OrderBy(j => j.UserMessageId)
            .ToListAsync(cancellationToken);

        return Ok(jobs.Select(j => new ReplyJobResponse
        {
            JobId = j.Id,
            UserMessageId = j.UserMessageId,
            Status = j.Status == AiReplyJobStatus.Failed ? "failed" : "pending",
            Error = j.Status == AiReplyJobStatus.Failed ? AiReplyProcessor.FailureMessage : null,
        }).ToList());
    }

    /// <summary>
    /// Asks for another go at a reply that failed. Re-queues the same job (no duplicate question). Idempotent for a
    /// job that is already pending; 409 for one that is done. 404 for someone else's job.
    /// </summary>
    [HttpPost("jobs/{jobId:guid}/retry")]
    [RateLimit(RateLimitPolicy.Chat)]
    public async Task<ActionResult<RetryReplyResponse>> RetryReply(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.AiReplyJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null || (job.UserId != User.GetUserId() && !User.IsAdmin()))
        {
            return NotFound(new ErrorResponse("job_not_found", "Reply not found."));
        }

        if (await GetOwnedSessionAsync(job.SessionId, cancellationToken) is null)
        {
            return NotFound(new ErrorResponse("job_not_found", "Reply not found.")); // its chat was deleted
        }

        if (job.Status == AiReplyJobStatus.Done)
        {
            return Conflict(new ErrorResponse("reply_already_delivered", "This reply was already delivered."));
        }

        if (job.Status == AiReplyJobStatus.Failed)
        {
            if (_replyQueue is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new ErrorResponse("ai_service_unavailable", "The AI service is temporarily unavailable. Try again shortly."));
            }

            job.Status = AiReplyJobStatus.Pending;
            job.Attempts = 0;
            job.Error = null;
            job.ClaimedAt = null;
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                await _replyQueue.EnqueueAsync(job.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not re-queue AI reply job {JobId}", job.Id);
                job.Status = AiReplyJobStatus.Failed;
                job.Error = "Could not queue the reply.";
                await _db.SaveChangesAsync(CancellationToken.None);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new ErrorResponse("ai_service_unavailable", "The AI service is temporarily unavailable. Try again shortly."));
            }
        }

        return Accepted(new RetryReplyResponse { JobId = job.Id, ReplyStatus = "pending" });
    }

    /// <summary>Null unless the session exists AND (the caller owns it OR the caller is Admin).</summary>
    private async Task<ChatSession?> GetOwnedSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAt == null, cancellationToken);
        if (session is null)
        {
            return null;
        }

        return session.UserId == User.GetUserId() || User.IsAdmin() ? session : null;
    }

    // 404, not 403, for "not yours" too - avoids confirming to a caller that a given session id
    // exists at all when it isn't theirs.
    private ActionResult SessionNotFound() => NotFound(new ErrorResponse("session_not_found", "Chat session not found."));

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int SearchMaxLength = 100;

    /// <summary>Missing or non-positive means the default; anything above the cap is cut to it.</summary>
    private static int PageSize(int? limit) => limit is null or <= 0 ? DefaultPageSize : Math.Min(limit.Value, MaxPageSize);

    private const string DefaultTitle = "New chat";
    private const int TitleMaxLength = 60;

    private static string TitleFromMessage(string content)
    {
        var singleLine = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length <= TitleMaxLength)
        {
            return singleLine;
        }

        var cut = singleLine[..(TitleMaxLength - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        // Break at a word boundary unless that would throw away most of the text.
        if (lastSpace > TitleMaxLength / 2)
        {
            cut = cut[..lastSpace];
        }
        return cut.TrimEnd() + "…";
    }

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
