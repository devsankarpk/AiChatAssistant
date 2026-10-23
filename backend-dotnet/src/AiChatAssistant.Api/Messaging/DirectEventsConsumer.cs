using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AiChatAssistant.Api.Messaging;

/// <summary>Consumes <c>direct.events.log</c> and hands each event to <see cref="DirectEventHandler"/>. An event it cannot read is dead-lettered, not retried forever.</summary>
public sealed class DirectEventsConsumer : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMqConnection _connection;
    private readonly DirectEventHandler _handler;
    private readonly ILogger<DirectEventsConsumer> _logger;

    public DirectEventsConsumer(RabbitMqConnection connection, DirectEventHandler handler, ILogger<DirectEventsConsumer> logger)
    {
        _connection = connection;
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        while (channel is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                channel = await _connection.CreateChannelAsync(confirmPublishes: false, stoppingToken);
                await DirectEventsTopology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 20, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var result = await _handler.HandleAsync(delivery.BasicProperties.Type, delivery.BasicProperties.MessageId, delivery.Body);
                        if (result == DirectEventResult.Invalid)
                        {
                            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, stoppingToken); // to direct.events.dead
                        }
                        else
                        {
                            await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // shutting down: the broker redelivers
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Direct event consumer failed on a message");
                        try { await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, CancellationToken.None); } catch { /* channel gone */ }
                    }
                };
                await channel.BasicConsumeAsync(DirectEventsTopology.LogQueue, autoAck: false, consumer, stoppingToken);
                _logger.LogInformation("Direct events consumer is consuming {Queue}", DirectEventsTopology.LogQueue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Direct events consumer could not reach RabbitMQ; trying again in {Seconds}s", ConnectRetryDelay.TotalSeconds);
                if (channel is not null)
                {
                    await channel.DisposeAsync();
                    channel = null;
                }
                await Task.Delay(ConnectRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
        }
    }
}
