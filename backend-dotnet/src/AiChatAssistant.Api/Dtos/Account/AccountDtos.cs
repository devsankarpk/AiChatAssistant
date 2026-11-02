using AiChatAssistant.Api.Dtos.Auth;

namespace AiChatAssistant.Api.Dtos.Account;

public class ProfileResponse
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public IReadOnlyCollection<string> Roles { get; set; } = Array.Empty<string>();

    public DateTime CreatedAt { get; set; }

    /// <summary>False only for someone who registered while email verification was required and has not confirmed yet (they cannot sign in until they do).</summary>
    public bool EmailVerified { get; set; }
}

public class UpdateProfileRequest
{
    public string Name { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

public class DeleteAccountRequest
{
    /// <summary>Asked for again as the confirmation: deleting an account cannot be undone, and a stolen session alone must not be enough.</summary>
    public string Password { get; set; } = string.Empty;
}
