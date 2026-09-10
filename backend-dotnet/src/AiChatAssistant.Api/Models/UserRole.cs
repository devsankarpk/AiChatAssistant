namespace AiChatAssistant.Api.Models;

/// <summary>Join entity for the many-to-many between <see cref="User"/> and <see cref="Role"/>.</summary>
public class UserRole
{
    public int UserId { get; set; }

    public int RoleId { get; set; }

    public User User { get; set; } = null!;

    public Role Role { get; set; } = null!;
}
