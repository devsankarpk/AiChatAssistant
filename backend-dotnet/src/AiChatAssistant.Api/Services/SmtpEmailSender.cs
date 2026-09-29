using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AiChatAssistant.Api.Services;

/// <summary>
/// Sends real email over SMTP via MailKit. Host/port/from-address come from appsettings.json
/// (Smtp:Host/Port/FromEmail/FromName, Gmail defaults), Username/Password are secrets - set via
/// 'dotnet user-secrets set "Smtp:Username" "<value>"' / "Smtp:Password" in Development, or
/// Smtp__Username / Smtp__Password environment variables elsewhere. For Gmail, Username is the
/// full address and Password is a 16-character App Password (myaccount.google.com/apppasswords),
/// not the account's real login password.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink, CancellationToken cancellationToken)
    {
        var host = _configuration["Smtp:Host"] ?? "smtp.gmail.com";
        var port = _configuration.GetValue("Smtp:Port", 587);
        var username = _configuration["Smtp:Username"]
            ?? throw new InvalidOperationException(
                "Smtp:Username is not configured. Set it via 'dotnet user-secrets set \"Smtp:Username\" \"<value>\"' in Development, or the Smtp__Username environment variable elsewhere.");
        var password = _configuration["Smtp:Password"]
            ?? throw new InvalidOperationException(
                "Smtp:Password is not configured. Set it via 'dotnet user-secrets set \"Smtp:Password\" \"<value>\"' in Development, or the Smtp__Password environment variable elsewhere.");
        var fromEmail = _configuration["Smtp:FromEmail"] ?? username;
        var fromName = _configuration["Smtp:FromName"] ?? "AI Chat Assistant";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = "Reset your AI Chat Assistant password";
        message.Body = new TextPart("plain")
        {
            Text = $"Hi {toName},\n\n" +
                   "We received a request to reset your AI Chat Assistant password. This link is valid for 1 hour:\n\n" +
                   $"{resetLink}\n\n" +
                   "If you didn't request this, you can safely ignore this email.",
        };

        using var client = new SmtpClient();
        // Revocation checking (OCSP/CRL) can fail to complete on some networks even though the
        // certificate itself is valid, which SslStream then treats as fatal. Disabling it here
        // only weakens revocation detection for this one pinned host (smtp.gmail.com) - the
        // certificate's chain of trust is still fully validated.
        client.CheckCertificateRevocation = false;
        await client.ConnectAsync(host, port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(username, password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation("Password reset email sent to {ToEmail}", toEmail);
    }
}
