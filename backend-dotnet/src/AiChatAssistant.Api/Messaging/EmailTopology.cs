using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// The queues behind password-reset email. A message goes
///   email (exchange) -> email.send -> worker; on failure -> email.retry.{delay}s (waits, then
///   dead-letters back to the exchange) -> email.send again; after the last retry -> email.dead.
/// Declaring is idempotent, so the publisher and the worker both do it and neither has to start first.
/// The retry queue's name carries its delay because RabbitMQ refuses to re-declare an existing queue
/// with different arguments: changing Email:RetryDelaysSeconds creates new queues instead of failing.
/// </summary>
public static class EmailTopology
{
    public const string Exchange = "email";
    public const string RoutingKey = "send";
    public const string SendQueue = "email.send";
    public const string DeadQueue = "email.dead";

    /// <summary>Failed emails are kept for inspection for a week, then RabbitMQ discards them.</summary>
    public static readonly int DeadLetterTtlMilliseconds = (int)TimeSpan.FromDays(7).TotalMilliseconds;

    public static string RetryQueue(int delaySeconds) => $"email.retry.{delaySeconds}s";

    public static async Task DeclareAsync(IChannel channel, EmailQueueOptions options, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(SendQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(SendQueue, Exchange, RoutingKey, arguments: null, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-message-ttl"] = DeadLetterTtlMilliseconds },
            cancellationToken: cancellationToken);

        foreach (var delay in options.RetryDelaysSeconds.Distinct())
        {
            await channel.QueueDeclareAsync(RetryQueue(delay), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = delay * 1000,
                    ["x-dead-letter-exchange"] = Exchange,
                    ["x-dead-letter-routing-key"] = RoutingKey,
                },
                cancellationToken: cancellationToken);
        }
    }
}
