using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Consumes <c>ai.reply</c>: for each job id it runs <see cref="AiReplyProcessor"/> and then records what happened
/// in the broker before acknowledging (parked in a retry queue, moved to the dead-letter queue, or nothing more).
/// Delivery is at-least-once, which is safe because the processor is idempotent. <c>Chat:ReplyPrefetch</c> caps
/// how many AI calls this process runs at once.
/// </summary>
public sealed class AiReplyWorker : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>A message that keeps crashing the worker itself (not the AI call) is dead-lettered after this many tries.</summary>
    private const int MaxUnexpectedFailures = 5;

    private readonly RabbitMqConnection _connection;
    private readonly IServiceScopeFactory _scopes;
    private readonly ChatOptions _options;
    private readonly ILogger<AiReplyWorker> _logger;

    public AiReplyWorker(
        RabbitMqConnection connection,
        IServiceScopeFactory scopes,
        IOptions<ChatOptions> options,
        ILogger<AiReplyWorker> logger)
    {
        _connection = connection;
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        while (channel is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                channel = await _connection.CreateChannelAsync(confirmPublishes: false, stoppingToken, consumerConcurrency: _options.ReplyPrefetch);
                await AiReplyTopology.DeclareAsync(channel, _options, stoppingToken);
                await channel.BasicQosAsync(0, (ushort)Math.Max(1, _options.ReplyPrefetch), false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(AiReplyTopology.ReplyQueue, autoAck: false, consumer, stoppingToken);
                _logger.LogInformation("AI reply worker is consuming {Queue} (prefetch {Prefetch})", AiReplyTopology.ReplyQueue, _options.ReplyPrefetch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "AI reply worker could not reach RabbitMQ; trying again in {Seconds}s", ConnectRetryDelay.TotalSeconds);
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

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var unexpectedFailures = ReadAttempt(delivery);
        try
        {
            AiReplyMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<AiReplyMessage>(delivery.Body.Span);
            }
            catch (JsonException)
            {
                message = null;
            }

            if (message is null || message.JobId == Guid.Empty)
            {
                await PublishToAsync(AiReplyTopology.DeadQueue, delivery, unexpectedFailures, "unreadable message body", stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                return;
            }

            await using var scope = _scopes.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<AiReplyProcessor>();
            var result = await processor.ProcessAsync(message.JobId, stoppingToken);

            switch (result.Outcome)
            {
                case AiReplyOutcome.Retry:
                case AiReplyOutcome.Deferred:
                    await PublishToAsync(AiReplyTopology.RetryQueue((int)result.Delay.TotalSeconds), delivery, unexpectedFailures, result.Error, stoppingToken);
                    break;
                case AiReplyOutcome.Failed:
                    await PublishToAsync(AiReplyTopology.DeadQueue, delivery, unexpectedFailures, result.Error, stoppingToken);
                    break;
                    // Done, AlreadyHandled and Missing need nothing more than the acknowledgement below.
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: leave the message unacknowledged so the broker redelivers it.
        }
        catch (Exception ex)
        {
            // Something outside the AI call went wrong (database down, broker dropped mid-handling). Park the job
            // and try again shortly instead of requeueing at once, which would spin; give up on a poison message.
            _logger.LogError(ex, "AI reply worker failed while handling a message (failure {Count})", unexpectedFailures + 1);
            try
            {
                var target = unexpectedFailures + 1 >= MaxUnexpectedFailures
                    ? AiReplyTopology.DeadQueue
                    : AiReplyTopology.RetryQueue(_options.ReplyLeaseSeconds);
                await PublishToAsync(target, delivery, unexpectedFailures + 1, ex.Message, CancellationToken.None);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, CancellationToken.None);
            }
            catch (Exception nested)
            {
                _logger.LogWarning(nested, "Could not park the message; returning it to the queue");
                try
                {
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, CancellationToken.None);
                }
                catch (Exception nackFailure)
                {
                    _logger.LogWarning(nackFailure, "Could not return the message to the queue; the broker will redeliver it when the channel closes");
                }
            }
        }
    }

    /// <summary>Copies the message to another queue via the default exchange, waiting for the broker's confirmation.</summary>
    private async Task PublishToAsync(string queue, BasicDeliverEventArgs delivery, int attempt, string? error, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, object?> { [RabbitMqAiReplyQueue.AttemptHeader] = attempt };
        if (error is not null)
        {
            headers["x-error"] = error.Length > 500 ? error[..500] : error;
        }

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };

        await _connection.WithTimeoutAsync(async token =>
        {
            await using var channel = await _connection.CreateChannelAsync(confirmPublishes: true, token);
            await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, basicProperties: properties,
                body: delivery.Body, cancellationToken: token);
        }, cancellationToken);
    }

    private static int ReadAttempt(BasicDeliverEventArgs delivery)
        => delivery.BasicProperties.Headers?.TryGetValue(RabbitMqAiReplyQueue.AttemptHeader, out var value) == true && value is int attempt
            ? attempt
            : 0;
}
