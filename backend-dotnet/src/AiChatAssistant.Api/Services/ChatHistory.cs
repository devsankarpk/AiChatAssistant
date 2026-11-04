using AiChatAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Services;

/// <summary>The context sent to the AI with each question. One implementation, used by every reply path, so they cannot drift apart.</summary>
public static class ChatHistory
{
    /// <summary>The most prior messages ever loaded for one question. Python is stateless and has no DB access (see CLAUDE.md), so this is a ceiling, not the usual size: the token budget below normally stops first.</summary>
    public const int Limit = 50;

    /// <summary>Default room for history plus the new question, in estimated tokens. Well under a typical 32k-token model window, so the reply (capped separately) always fits.</summary>
    public const int DefaultTokenBudget = 6000;

    /// <summary>Rough cost of one message in tokens: about four characters a token, plus a little for the role framing.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4 + 4;

    /// <summary>
    /// The messages before <paramref name="beforeMessageId"/>, oldest first: as many of the newest ones as fit in
    /// <paramref name="tokenBudget"/> once the new question (<paramref name="promptText"/>) has been counted, at most <see cref="Limit"/>.
    /// The newest messages always win; older ones are dropped whole, never cut in half, and never skipped over
    /// (history with a hole in it would make the conversation incoherent).
    /// </summary>
    public static async Task<List<PythonHistoryMessage>> LoadAsync(
        AppDbContext db, Guid sessionId, int beforeMessageId, CancellationToken cancellationToken,
        int tokenBudget = DefaultTokenBudget, string promptText = "")
    {
        var prior = await db.Messages
            .Where(m => m.SessionId == sessionId && m.Id < beforeMessageId)
            .OrderByDescending(m => m.Id)
            .Take(Limit)
            .ToListAsync(cancellationToken);

        var remaining = tokenBudget - EstimateTokens(promptText);
        var kept = new List<PythonHistoryMessage>();
        foreach (var m in prior) // newest first
        {
            var cost = EstimateTokens(m.Content);
            if (cost > remaining)
            {
                break;
            }
            remaining -= cost;
            kept.Add(new PythonHistoryMessage(m.Role.ToString().ToLowerInvariant(), m.Content));
        }

        kept.Reverse(); // chronological order (oldest first) for the LLM
        return kept;
    }
}
