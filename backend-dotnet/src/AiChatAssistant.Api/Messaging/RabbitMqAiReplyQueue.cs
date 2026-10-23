using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>Publishes a job id to <c>ai.reply</c>, persistent and confirmed by the broker.</summary>
public sealed class RabbitMqAiReplyQueue : IAiReplyQueue
{
    public const string AttemptHeader = "x-attempt";

    private readonly RabbitMqConnection _connection;
    private readonly ChatOptions _options;

    public RabbitMqAiReplyQueue(RabbitMqConnection connection, IOptions<ChatOptions> options)
    {
        _connection = connection;
        _options = options.Value;
    }

    public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?> { [AttemptHeader] = 0 },
        };

        await _connection.WithTimeoutAsync(async token =>
        {
            await using var channel = await _connection.CreateChannelAsync(confirmPublishes: true, token);
            await AiReplyTopology.DeclareAsync(channel, _options, token);
            await channel.BasicPublishAsync(AiReplyTopology.Exchange, AiReplyTopology.RoutingKey, mandatory: false,
                basicProperties: properties, body: JsonSerializer.SerializeToUtf8Bytes(new AiReplyMessage(jobId)),
                cancellationToken: token);
        }, cancellationToken);
    }
}

/// <summary>The whole body of a queued job: its id. The question and the answer stay in the database.</summary>
public sealed record AiReplyMessage(Guid JobId);
