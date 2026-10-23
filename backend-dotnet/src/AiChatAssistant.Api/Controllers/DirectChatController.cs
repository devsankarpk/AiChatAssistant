using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Controllers;

/// <summary>
/// One-to-one chat between registered users (separate from the AI chatbot in <see cref="ChatController"/>).
/// Direct messages are private: only the two participants can touch a conversation, and there is
/// deliberately no Admin exception - anyone who isn't a participant gets 404, admins included.
/// </summary>
[ApiController]
[Route("api/direct")]
[Authorize]
public class DirectChatController : ControllerBase
{
    private const int MinSearchLength = 2;
    private const int MaxSearchResults = 20;
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    private readonly IValidator<StartConversationRequest> _startValidator;
    private readonly IValidator<SendDirectMessageRequest> _sendValidator;
    private readonly IPresenceTracker _presence;
    private readonly IDirectNotifier _notifier;

    public DirectChatController(
        AppDbContext db,
        IValidator<StartConversationRequest> startValidator,
        IValidator<SendDirectMessageRequest> sendValidator,
        IPresenceTracker presence,
        IDirectNotifier notifier)
    {
        _db = db;
        _startValidator = startValidator;
        _sendValidator = sendValidator;
        _presence = presence;
        _notifier = notifier;
    }

    /// <summary>Find people to message by name. Matches the name only, so an email can never be probed.</summary>
    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<UserSearchResult>>> SearchUsers([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var term = (search ?? string.Empty).Trim();
        if (term.Length < MinSearchLength)
        {
            return BadRequest(new ErrorResponse("validation_error", $"Type at least {MinSearchLength} characters to search."));
        }

        var callerId = User.GetUserId();
        var lowered = term.ToLowerInvariant();
        var people = await _db.Users
            .Where(u => u.Id != callerId && u.Name.ToLower().Contains(lowered))
            .OrderBy(u => u.Name)
            .ThenBy(u => u.Id)
            .Take(MaxSearchResults)
            .Select(u => new UserSearchResult { Id = u.Id, Name = u.Name })
            .ToListAsync(cancellationToken);

        return Ok(people);
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<ConversationResponse>>> ListConversations(CancellationToken cancellationToken)
    {
        var callerId = User.GetUserId();

        var rows = await _db.DirectConversations
            .Where(c => c.UserLowId == callerId || c.UserHighId == callerId)
            .Select(c => new
            {
                c.Id,
                c.CreatedAt,
                PeerId = c.UserLowId == callerId ? c.UserHighId : c.UserLowId,
                PeerName = c.UserLowId == callerId ? c.UserHigh.Name : c.UserLow.Name,
                PeerLastSeenAt = c.UserLowId == callerId ? c.UserHigh.LastSeenAt : c.UserLow.LastSeenAt,
                Last = c.Messages
                    .OrderByDescending(m => m.Id)
                    .Select(m => new { m.Id, m.SenderId, m.Content, m.CreatedAt, m.DeliveredAt, m.ReadAt, m.DeletedAt })
                    .FirstOrDefault(),
                Unread = c.Messages.Count(m => m.SenderId != callerId && m.ReadAt == null && m.DeletedAt == null),
            })
            .ToListAsync(cancellationToken);

        var online = await _presence.OnlineAmongAsync(rows.Select(r => r.PeerId).ToList(), cancellationToken);
        var conversations = rows
            .OrderByDescending(r => r.Last != null ? r.Last.CreatedAt : r.CreatedAt)
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => new ConversationResponse
            {
                Id = r.Id,
                Peer = new PeerResponse { Id = r.PeerId, Name = r.PeerName },
                LastMessage = r.Last is null
                    ? null
                    : ToMessageResponse(r.Id, r.Last.Id, r.Last.SenderId, r.Last.Content, r.Last.CreatedAt, r.Last.DeliveredAt, r.Last.ReadAt, r.Last.DeletedAt),
                UnreadCount = r.Unread,
                PeerOnline = online.Contains(r.PeerId),
                PeerLastSeenAt = r.PeerLastSeenAt,
            })
            .ToList();

        return Ok(conversations);
    }

    /// <summary>
    /// Start (or reopen) a conversation with another user. Idempotent: the same pair always maps to
    /// the same conversation, whoever starts it. 201 when created, 200 when it already existed.
    /// </summary>
    [HttpPost("conversations")]
    public async Task<ActionResult<ConversationResponse>> StartConversation(StartConversationRequest request, CancellationToken cancellationToken)
    {
        var validation = await _startValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var callerId = User.GetUserId();
        if (request.UserId == callerId)
        {
            return BadRequest(new ErrorResponse("cannot_message_self", "You can't start a chat with yourself."));
        }

        var peer = await _db.Users
            .Where(u => u.Id == request.UserId)
            .Select(u => new { u.Id, u.Name, u.LastSeenAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (peer is null)
        {
            return NotFound(new ErrorResponse("user_not_found", "That person was not found."));
        }

        var low = Math.Min(callerId, peer.Id);
        var high = Math.Max(callerId, peer.Id);

        var conversation = await _db.DirectConversations
            .FirstOrDefaultAsync(c => c.UserLowId == low && c.UserHighId == high, cancellationToken);
        var created = false;
        if (conversation is null)
        {
            conversation = new DirectConversation { Id = Guid.NewGuid(), UserLowId = low, UserHighId = high };
            _db.DirectConversations.Add(conversation);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                created = true;
            }
            catch (DbUpdateException)
            {
                // Both people started the chat at the same moment and the unique index let only one
                // row in. Reopen the winner instead of failing.
                _db.Entry(conversation).State = EntityState.Detached;
                conversation = await _db.DirectConversations
                    .FirstOrDefaultAsync(c => c.UserLowId == low && c.UserHighId == high, cancellationToken);
                if (conversation is null)
                {
                    throw;
                }
            }
        }

        var unread = await _db.DirectMessages.CountAsync(
            m => m.ConversationId == conversation.Id && m.SenderId != callerId && m.ReadAt == null && m.DeletedAt == null,
            cancellationToken);
        var last = await _db.DirectMessages
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var response = new ConversationResponse
        {
            Id = conversation.Id,
            Peer = new PeerResponse { Id = peer.Id, Name = peer.Name },
            LastMessage = last is null ? null : ToMessageResponse(last),
            UnreadCount = unread,
            PeerOnline = await _presence.IsOnlineAsync(peer.Id, cancellationToken),
            PeerLastSeenAt = peer.LastSeenAt,
        };

        return created ? StatusCode(StatusCodes.Status201Created, response) : Ok(response);
    }

    /// <summary>
    /// Messages in a conversation, oldest first within the page. Newest page by default; pass
    /// <c>before=&lt;messageId&gt;</c> to load the page of older messages before that one.
    /// </summary>
    [HttpGet("conversations/{id:guid}/messages")]
    public async Task<ActionResult<IReadOnlyList<DirectMessageResponse>>> GetMessages(
        Guid id, [FromQuery] long? before, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var conversation = await GetParticipantConversationAsync(id, cancellationToken);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        var pageSize = limit is null or <= 0 ? DefaultPageSize : Math.Min(limit.Value, MaxPageSize);

        var query = _db.DirectMessages.Where(m => m.ConversationId == id);
        if (before is not null)
        {
            query = query.Where(m => m.Id < before);
        }

        var page = await query
            .OrderByDescending(m => m.Id)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        page.Reverse(); // chronological (oldest first) for display

        return Ok(page.Select(ToMessageResponse).ToList());
    }

    [HttpPost("conversations/{id:guid}/messages")]
    public async Task<ActionResult<DirectMessageResponse>> SendMessage(Guid id, SendDirectMessageRequest request, CancellationToken cancellationToken)
    {
        var validation = await _sendValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.ValidationError(validation);
        }

        var conversation = await GetParticipantConversationAsync(id, cancellationToken);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        var callerId = User.GetUserId();
        var recipientId = conversation.UserLowId == callerId ? conversation.UserHighId : conversation.UserLowId;

        var message = new DirectMessage
        {
            ConversationId = id,
            SenderId = callerId,
            Content = request.Content.Trim(),
        };
        _db.DirectMessages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        // One grey tick (sent) is the message being saved. Two grey ticks (delivered) is the recipient
        // having a live connection right now; if they connect later, the hub marks it delivered then.
        if (await _presence.IsOnlineAsync(recipientId, cancellationToken))
        {
            message.DeliveredAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var response = ToMessageResponse(message);
        await _notifier.MessageReceivedAsync(recipientId, response, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// The caller has this conversation open and visible: mark everything the other person sent as
    /// read (two blue ticks for them). Idempotent; reports how many messages were newly read.
    /// </summary>
    [HttpPost("conversations/{id:guid}/read")]
    public async Task<ActionResult<MarkReadResponse>> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        var conversation = await GetParticipantConversationAsync(id, cancellationToken);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        var callerId = User.GetUserId();
        var unread = await _db.DirectMessages
            .Where(m => m.ConversationId == id && m.SenderId != callerId && m.ReadAt == null && m.DeletedAt == null)
            .ToListAsync(cancellationToken);

        if (unread.Count == 0)
        {
            return Ok(new MarkReadResponse { ConversationId = id, ReadCount = 0, UpToMessageId = null });
        }

        var now = DateTime.UtcNow;
        foreach (var message in unread)
        {
            message.ReadAt = now;
            message.DeliveredAt ??= now; // reading implies it was delivered
        }
        await _db.SaveChangesAsync(cancellationToken);

        var peerId = conversation.UserLowId == callerId ? conversation.UserHighId : conversation.UserLowId;
        var ids = unread.Select(m => m.Id).OrderBy(i => i).ToList();
        var upTo = ids[^1];

        await _notifier.MessagesStatusChangedAsync(peerId, id, ids, "read", cancellationToken);
        await _notifier.MessagesReadAsync(callerId, id, upTo, cancellationToken);

        return Ok(new MarkReadResponse { ConversationId = id, ReadCount = ids.Count, UpToMessageId = upTo });
    }

    /// <summary>
    /// Delete one of your own messages for everyone. The text is cleared on the server (it is really gone,
    /// not just hidden), and both people see "This message was deleted". Idempotent: deleting it again is
    /// a no-op. Only the sender may delete; the other participant gets 403, outsiders and Admins get 404.
    /// </summary>
    [HttpDelete("conversations/{id:guid}/messages/{messageId:long}")]
    public async Task<ActionResult<DirectMessageResponse>> DeleteMessage(Guid id, long messageId, CancellationToken cancellationToken)
    {
        var conversation = await GetParticipantConversationAsync(id, cancellationToken);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        var message = await _db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id, cancellationToken);
        if (message is null)
        {
            return NotFound(new ErrorResponse("message_not_found", "Message not found."));
        }

        var callerId = User.GetUserId();
        if (message.SenderId != callerId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse("forbidden", "You can only delete your own messages."));
        }

        if (message.DeletedAt is null)
        {
            message.DeletedAt = DateTime.UtcNow;
            message.Content = string.Empty;
            await _db.SaveChangesAsync(cancellationToken);

            var peerId = conversation.UserLowId == callerId ? conversation.UserHighId : conversation.UserLowId;
            await _notifier.MessageDeletedAsync(peerId, id, messageId, cancellationToken);
            await _notifier.MessageDeletedAsync(callerId, id, messageId, cancellationToken); // the sender's other tabs
        }

        return Ok(ToMessageResponse(message));
    }

    /// <summary>
    /// The conversation, but only if the caller is one of its two participants. Anyone else - including
    /// an Admin - gets null (so 404, never 403: we don't reveal that the conversation exists).
    /// </summary>
    private Task<DirectConversation?> GetParticipantConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        var callerId = User.GetUserId();
        return _db.DirectConversations.FirstOrDefaultAsync(
            c => c.Id == id && (c.UserLowId == callerId || c.UserHighId == callerId),
            cancellationToken);
    }

    private NotFoundObjectResult ConversationNotFound() =>
        NotFound(new ErrorResponse("conversation_not_found", "Conversation not found."));

    private static DirectMessageResponse ToMessageResponse(DirectMessage m) =>
        ToMessageResponse(m.ConversationId, m.Id, m.SenderId, m.Content, m.CreatedAt, m.DeliveredAt, m.ReadAt, m.DeletedAt);

    private static DirectMessageResponse ToMessageResponse(
        Guid conversationId, long id, int senderId, string content, DateTime createdAt,
        DateTime? deliveredAt, DateTime? readAt, DateTime? deletedAt)
    {
        var deleted = deletedAt is not null;
        return new DirectMessageResponse
        {
            Id = id,
            ConversationId = conversationId,
            SenderId = senderId,
            Content = deleted ? string.Empty : content,
            CreatedAt = createdAt,
            Status = readAt is not null ? "read" : deliveredAt is not null ? "delivered" : "sent",
            Deleted = deleted,
        };
    }
}
