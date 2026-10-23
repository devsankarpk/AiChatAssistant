using AiChatAssistant.Api.Services;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Hands a password-reset email to whatever delivers it. With RabbitMQ it is queued (the request
/// returns at once and the worker sends, retries and dead-letters); without a broker it is sent
/// right here, as it always was. Callers cannot tell the difference, which is the point.
/// </summary>
public interface IEmailQueue
{
    Task EnqueuePasswordResetAsync(string toEmail, string toName, string resetLink, CancellationToken cancellationToken);
}

/// <summary>The no-broker path: send immediately through <see cref="IEmailSender"/>.</summary>
public sealed class InlineEmailQueue : IEmailQueue
{
    private readonly IEmailSender _sender;

    public InlineEmailQueue(IEmailSender sender)
    {
        _sender = sender;
    }

    public Task EnqueuePasswordResetAsync(string toEmail, string toName, string resetLink, CancellationToken cancellationToken)
        => _sender.SendPasswordResetEmailAsync(toEmail, toName, resetLink, cancellationToken);
}
