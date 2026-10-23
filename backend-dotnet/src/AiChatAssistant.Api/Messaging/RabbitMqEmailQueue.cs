using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Publishes a password-reset email to RabbitMQ. The publish is confirmed: this returns only after
/// the broker has durably accepted the message, and throws if it has not (broker down, auth failure),
/// so the caller never believes an email is queued when it is not.
/// </summary>
public sealed class RabbitMqEmailQueue : IEmailQueue
{
    public const string AttemptHeader = "x-attempt";
    public const string ExpiresAtHeader = "x-expires-at";

    private readonly RabbitMqConnection _connection;
    private readonly EmailQueueOptions _options;
    private readonly TimeProvider _time;

    public RabbitMqEmailQueue(RabbitMqConnection connection, IOptions<EmailQueueOptions> options, TimeProvider time)
    {
        _connection = connection;
        _options = options.Value;
        _time = time;
    }

    public async Task EnqueuePasswordResetAsync(string toEmail, string toName, string resetLink, CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromMinutes(_options.MessageTtlMinutes);
        var body = JsonSerializer.SerializeToUtf8Bytes(new PasswordResetEmail(toEmail, toName, resetLink));
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            // RabbitMQ drops a stale message at the head of the queue by itself; the worker also checks
            // the absolute time below, because a message that went through a retry queue loses this property.
            Expiration = ((long)ttl.TotalMilliseconds).ToString(),
            Headers = new Dictionary<string, object?>
            {
                [AttemptHeader] = 0,
                [ExpiresAtHeader] = _time.GetUtcNow().Add(ttl).ToUnixTimeMilliseconds(),
            },
        };

        // One short-lived channel per publish: channels are cheap and are not safe for concurrent publishing.
        await using var channel = await _connection.CreateChannelAsync(confirmPublishes: true, cancellationToken);
        // Declare first so the message is routable even if the worker has not started yet.
        await EmailTopology.DeclareAsync(channel, _options, cancellationToken);
        await channel.BasicPublishAsync(EmailTopology.Exchange, EmailTopology.RoutingKey, mandatory: false,
            basicProperties: properties, body: body, cancellationToken: cancellationToken);
    }
}
