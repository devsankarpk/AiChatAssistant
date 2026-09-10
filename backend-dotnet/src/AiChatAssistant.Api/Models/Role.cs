namespace AiChatAssistant.Api.Models;

public class Role
{
    public const string Admin = "Admin";
    public const string User = "User";

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
