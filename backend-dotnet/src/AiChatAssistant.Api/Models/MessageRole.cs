namespace AiChatAssistant.Api.Models;

/// <summary>Author of a chat message. Persisted as the lowercase string 'user' / 'assistant'.</summary>
public enum MessageRole
{
    User,
    Assistant
}
