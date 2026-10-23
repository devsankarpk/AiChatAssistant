using AiChatAssistant.Api.Models;
using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>Publishes one outbox row and returns only once the broker has accepted it; throws otherwise. Behind an interface so the dispatcher's claim logic is testable without a broker.</summary>
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage row, CancellationToken cancellationToken);
}

/// <summary>Publishes to the <c>direct.events</c> topic exchange with the event type as routing key and the outbox id as message id, persistent and confirmed.</summary>
public sealed class RabbitMqOutboxPublisher : IOutboxPublisher, IAsyncDisposable
{
    private readonly RabbitMqConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public RabbitMqOutboxPublisher(RabbitMqConnection connection)
    {
        _connection = connection;
    }

    public async Task PublishAsync(OutboxMessage row, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = row.Id.ToString(),
            Type = row.Type,
            Timestamp = new AmqpTimestamp(new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds()),
        };

        // One confirm channel is reused (publishing on a channel is serialized here because it is not safe to share).
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _connection.WithTimeoutAsync(async token =>
            {
                if (_channel is not { IsOpen: true })
                {
                    if (_channel is not null)
                    {
                        await _channel.DisposeAsync();
                        _channel = null;
                    }
                    var channel = await _connection.CreateChannelAsync(confirmPublishes: true, token);
                    await DirectEventsTopology.DeclareAsync(channel, token);
                    _channel = channel;
                }

                await _channel.BasicPublishAsync(DirectEventsTopology.Exchange, row.Type, mandatory: false,
                    basicProperties: properties, body: System.Text.Encoding.UTF8.GetBytes(row.Payload), cancellationToken: token);
            }, cancellationToken);
        }
        catch (BrokerUnavailableException)
        {
            // A channel that timed out may be stuck mid-publish; start from a fresh one next time.
            var stuck = _channel;
            _channel = null;
            if (stuck is not null)
            {
                _ = stuck.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }
        _gate.Dispose();
    }
}
