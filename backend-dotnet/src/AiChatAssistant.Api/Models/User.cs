namespace AiChatAssistant.Api.Models;

public class User
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();

    public ICollection<UsageLog> UsageLogs { get; set; } = new List<UsageLog>();
}
