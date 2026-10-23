using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// <c>direct.events</c> is a topic exchange, so any future subscriber can bind its own queue to the events it
/// wants (<c>direct.message.*</c>, <c>#</c>, ...). Version 1 ships one subscriber: <c>direct.events.log</c>, which
/// logs and counts every event. A message that queue cannot process is dead-lettered to <c>direct.events.dead</c>.
/// </summary>
public static class DirectEventsTopology
{
    public const string Exchange = "direct.events";
    public const string LogQueue = "direct.events.log";
    public const string DeadQueue = "direct.events.dead";

    public static readonly int DeadLetterTtlMilliseconds = (int)TimeSpan.FromDays(7).TotalMilliseconds;

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-message-ttl"] = DeadLetterTtlMilliseconds },
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(LogQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = string.Empty, // the default exchange routes by queue name
                ["x-dead-letter-routing-key"] = DeadQueue,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(LogQueue, Exchange, "#", arguments: null, cancellationToken: cancellationToken);
    }
}
