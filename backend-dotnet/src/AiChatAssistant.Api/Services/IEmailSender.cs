namespace AiChatAssistant.Api.Services;

/// <summary>
/// The one seam a real email provider (SendGrid, Resend, Postmark, SES, ...) plugs into.
/// Swap the DI registration in Program.cs the same way DeepInfra could be swapped for a
/// different LLM provider - nothing else in AuthController needs to change.
/// </summary>
public interface IEmailSender
{
    Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink, CancellationToken cancellationToken);
}
