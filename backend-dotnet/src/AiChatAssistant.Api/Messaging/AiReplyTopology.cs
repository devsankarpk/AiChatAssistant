using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// The queues behind async AI replies: exchange <c>ai</c> to queue <c>ai.reply</c> to the worker; a failed
/// attempt waits in <c>ai.retry.{delay}s</c> (which dead-letters back to <c>ai.reply</c>); after the last
/// attempt the job id goes to <c>ai.dead</c> for inspection. Only a job id travels through these queues.
/// Retry queue names carry their delay for the same reason as in <see cref="EmailTopology"/>.
/// </summary>
public static class AiReplyTopology
{
    public const string Exchange = "ai";
    public const string RoutingKey = "reply";
    public const string ReplyQueue = "ai.reply";
    public const string DeadQueue = "ai.dead";

    public static readonly int DeadLetterTtlMilliseconds = (int)TimeSpan.FromDays(7).TotalMilliseconds;

    public static string RetryQueue(int delaySeconds) => $"ai.retry.{delaySeconds}s";

    /// <summary>Every delay a message can be parked for: the configured retry delays and the lease (used to defer a busy job).</summary>
    public static IEnumerable<int> RetryDelays(ChatOptions options) =>
        options.ReplyRetryDelaysSeconds.Append(options.ReplyLeaseSeconds).Distinct();

    public static async Task DeclareAsync(IChannel channel, ChatOptions options, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(ReplyQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(ReplyQueue, Exchange, RoutingKey, arguments: null, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-message-ttl"] = DeadLetterTtlMilliseconds },
            cancellationToken: cancellationToken);

        foreach (var delay in RetryDelays(options))
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
