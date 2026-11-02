namespace AiChatAssistant.Api.Dtos.Auth;

public class VerifyEmailRequest
{
    public string Token { get; set; } = string.Empty;
}

public class ResendVerificationRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// The answer to registering while email verification is required: the same for a new address and for one that already has an account,
/// so registration cannot be used to find out which emails are registered. No token: the person confirms first, then logs in.
/// </summary>
public class RegistrationPendingResponse
{
    public string Message { get; set; } = "Check your email to confirm your address, then log in.";

    public bool VerificationRequired { get; set; } = true;
}
