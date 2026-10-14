namespace AiChatAssistant.Api.Models;

/// <summary>
/// A one-to-one conversation between two users (separate from the AI chatbot's <see cref="ChatSession"/>).
/// The pair is always stored with the lower user id first, so there is exactly one row per pair
/// (unique index) and "start a chat" is idempotent no matter who starts it.
/// </summary>
public class DirectConversation
{
    public Guid Id { get; set; }

    public int UserLowId { get; set; }

    public int UserHighId { get; set; }

    public DateTime CreatedAt { get; set; }

    public User UserLow { get; set; } = null!;

    public User UserHigh { get; set; } = null!;

    public ICollection<DirectMessage> Messages { get; set; } = new List<DirectMessage>();
}
