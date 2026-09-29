namespace AiChatAssistant.Api.Services;

/// <summary>
/// Dev-only stand-in: logs the reset link instead of sending a real email. No email provider is
/// wired into this project yet - copy the link out of the console/log to actually test the flow.
/// Before deploying anywhere real users can reach, register a real IEmailSender implementation
/// (SendGrid/Resend/Postmark/SES/...) in Program.cs instead of this one.
/// </summary>
public class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "[DEV EMAIL - not actually sent] To: {ToName} <{ToEmail}> | Subject: Reset your AI Chat Assistant password | Link: {ResetLink} | " +
            "This is a dev-only stub - see ConsoleEmailSender for how to swap in a real provider.",
            toName, toEmail, resetLink);
        return Task.CompletedTask;
    }
}
