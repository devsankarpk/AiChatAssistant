namespace AiChatAssistant.Api.Models;

/// <summary>
/// A message between two users. The tick state is derived from the timestamps, never stored:
/// <c>ReadAt</c> set = read, else <c>DeliveredAt</c> set = delivered, else sent. Deleting a message
/// sets <c>DeletedAt</c> and clears <c>Content</c> on the server (Phase 13).
/// </summary>
public class DirectMessage
{
    public long Id { get; set; }

    public Guid ConversationId { get; set; }

    public int SenderId { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public DateTime? ReadAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DirectConversation Conversation { get; set; } = null!;

    public User Sender { get; set; } = null!;
}
