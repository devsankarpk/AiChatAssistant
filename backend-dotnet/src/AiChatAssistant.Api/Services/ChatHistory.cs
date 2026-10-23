using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

/// <summary>The context sent to the AI with each question. One implementation, used by the request path and by the async worker, so they cannot drift apart.</summary>
public static class ChatHistory
{
    /// <summary>How many prior messages to send Python as context on each turn. Python is stateless and has no DB access (see CLAUDE.md).</summary>
    public const int Limit = 20;

    /// <summary>The messages before <paramref name="beforeMessageId"/>, oldest first, at most <see cref="Limit"/>.</summary>
    public static async Task<List<PythonHistoryMessage>> LoadAsync(AppDbContext db, Guid sessionId, int beforeMessageId, CancellationToken cancellationToken)
    {
        var prior = await db.Messages
            .Where(m => m.SessionId == sessionId && m.Id < beforeMessageId)
            .OrderByDescending(m => m.Id)
            .Take(Limit)
            .ToListAsync(cancellationToken);
        prior.Reverse(); // chronological order (oldest first) for the LLM

        return prior
            .Select(m => new PythonHistoryMessage(m.Role.ToString().ToLowerInvariant(), m.Content))
            .ToList();
    }
}
