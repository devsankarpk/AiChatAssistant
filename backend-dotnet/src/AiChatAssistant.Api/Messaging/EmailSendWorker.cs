using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Consumes <c>email.send</c> and sends each password-reset email through <see cref="PasswordResetEmailProcessor"/>.
/// Delivery is at-least-once: a message is acknowledged only after its outcome is safely recorded
/// (sent, republished to a retry queue, or republished to the dead-letter queue), so a crash mid-send
/// redelivers it. The cost is that a crash between sending and acknowledging can send one email twice.
/// </summary>
public sealed class EmailSendWorker : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMqConnection _connection;
    private readonly IServiceScopeFactory _scopes;
    private readonly EmailQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailSendWorker> _logger;

    public EmailSendWorker(
        RabbitMqConnection connection,
        IServiceScopeFactory scopes,
        IOptions<EmailQueueOptions> options,
        TimeProvider time,
        ILogger<EmailSendWorker> logger)
    {
        _connection = connection;
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        // The broker may start after the API (or be down); keep trying instead of crashing the host.
        while (channel is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                channel = await _connection.CreateChannelAsync(confirmPublishes: false, stoppingToken);
                await EmailTopology.DeclareAsync(channel, _options, stoppingToken);
                await channel.BasicQosAsync(0, (ushort)Math.Max(1, _options.Prefetch), false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(EmailTopology.SendQueue, autoAck: false, consumer, stoppingToken);
                _logger.LogInformation("Email worker is consuming {Queue}", EmailTopology.SendQueue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Email worker could not reach RabbitMQ; trying again in {Seconds}s", ConnectRetryDelay.TotalSeconds);
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
        PasswordResetEmail? message;
        try
        {
            message = JsonSerializer.Deserialize<PasswordResetEmail>(delivery.Body.Span);
        }
        catch (JsonException)
        {
            message = null;
        }

        try
        {
            if (message is null)
            {
                await PublishToAsync(EmailTopology.DeadQueue, delivery, attempt: ReadAttempt(delivery), "unreadable message body", stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                return;
            }

            var attempt = ReadAttempt(delivery);
            var expiresAt = ReadExpiresAt(delivery);

            await using var scope = _scopes.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<PasswordResetEmailProcessor>();
            var result = await processor.ProcessAsync(message, attempt, expiresAt, stoppingToken);

            switch (result.Outcome)
            {
                case EmailOutcome.Retry:
                    await PublishToAsync(EmailTopology.RetryQueue((int)result.Delay.TotalSeconds), delivery, attempt + 1, result.Error, stoppingToken);
                    break;
                case EmailOutcome.Dead:
                    await PublishToAsync(EmailTopology.DeadQueue, delivery, attempt + 1, result.Error, stoppingToken);
                    break;
                    // Sent and Expired need nothing more than the acknowledgement below.
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: leave the message unacknowledged so the broker redelivers it.
        }
        catch (Exception ex)
        {
            // Could not record the outcome (e.g. the broker dropped mid-handling): put it back, never lose it.
            _logger.LogError(ex, "Email worker failed while handling a message; returning it to the queue");
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

    /// <summary>Copies the message to another queue via the default exchange, waiting for the broker's confirmation.</summary>
    private async Task PublishToAsync(string queue, BasicDeliverEventArgs delivery, int attempt, string? error, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, object?>
        {
            [RabbitMqEmailQueue.AttemptHeader] = attempt,
            [RabbitMqEmailQueue.ExpiresAtHeader] = ReadExpiresAt(delivery).ToUnixTimeMilliseconds(),
        };
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

        await using var channel = await _connection.CreateChannelAsync(confirmPublishes: true, cancellationToken);
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, basicProperties: properties,
            body: delivery.Body, cancellationToken: cancellationToken);
    }

    private static int ReadAttempt(BasicDeliverEventArgs delivery)
        => delivery.BasicProperties.Headers?.TryGetValue(RabbitMqEmailQueue.AttemptHeader, out var value) == true && value is int attempt
            ? attempt
            : 0;

    private DateTimeOffset ReadExpiresAt(BasicDeliverEventArgs delivery)
        => delivery.BasicProperties.Headers?.TryGetValue(RabbitMqEmailQueue.ExpiresAtHeader, out var value) == true && value is long ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            // No header (should not happen): fall back to the configured lifetime from now.
            : _time.GetUtcNow().AddMinutes(_options.MessageTtlMinutes);
}
