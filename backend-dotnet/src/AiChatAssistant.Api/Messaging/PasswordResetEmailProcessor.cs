using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Messaging;

public enum EmailOutcome
{
    /// <summary>The email was sent. Acknowledge and forget.</summary>
    Sent,

    /// <summary>Sending failed but retries remain. Publish to the retry queue for <see cref="EmailResult.Delay"/>.</summary>
    Retry,

    /// <summary>Sending failed and no retries remain. Move to the dead-letter queue.</summary>
    Dead,

    /// <summary>The link inside is past its lifetime. Drop it; sending it would only confuse the person.</summary>
    Expired,
}

public sealed record EmailResult(EmailOutcome Outcome, TimeSpan Delay = default, string? Error = null);

/// <summary>
/// The decision logic of the email worker, kept free of RabbitMQ so it can be tested without a
/// broker: given a message and how many times it has already failed, send it and say what to do next.
/// </summary>
public sealed class PasswordResetEmailProcessor
{
    private readonly IEmailSender _sender;
    private readonly EmailQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PasswordResetEmailProcessor> _logger;

    public PasswordResetEmailProcessor(
        IEmailSender sender,
        IOptions<EmailQueueOptions> options,
        TimeProvider time,
        ILogger<PasswordResetEmailProcessor> logger)
    {
        _sender = sender;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <param name="failedAttempts">How many sends of this message have already failed (0 for the first try).</param>
    public async Task<EmailResult> ProcessAsync(
        PasswordResetEmail message, int failedAttempts, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() >= expiresAt)
        {
            _logger.LogWarning("Dropping an expired password reset email (never sent)");
            return new EmailResult(EmailOutcome.Expired);
        }

        try
        {
            await _sender.SendPasswordResetEmailAsync(message.ToEmail, message.ToName, message.ResetLink, cancellationToken);
            return new EmailResult(EmailOutcome.Sent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var delays = _options.RetryDelaysSeconds;
            if (failedAttempts < delays.Length)
            {
                var delay = TimeSpan.FromSeconds(delays[failedAttempts]);
                _logger.LogWarning(ex, "Password reset email failed (attempt {Attempt} of {Total}); retrying in {Delay}s",
                    failedAttempts + 1, delays.Length + 1, delay.TotalSeconds);
                return new EmailResult(EmailOutcome.Retry, delay, ex.Message);
            }

            _logger.LogError(ex, "Password reset email failed (attempt {Attempt} of {Total}); giving up and dead-lettering it",
                failedAttempts + 1, delays.Length + 1);
            return new EmailResult(EmailOutcome.Dead, Error: ex.Message);
        }
    }
}
