using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// One shared, lazily opened RabbitMQ connection for the whole process (a connection is expensive,
/// a channel is cheap). Automatic recovery is on, so a broker restart reconnects and re-declares
/// everything by itself. Nothing connects until the first publish or until the worker starts, so the
/// API boots fine when the broker is down.
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// A new channel. With <paramref name="confirmPublishes"/> a publish only completes once the broker
    /// has safely accepted the message (and throws when it has not), which is what lets the caller
    /// trust that "published" means "queued".
    /// </summary>
    public async Task<IChannel> CreateChannelAsync(bool confirmPublishes, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var channelOptions = confirmPublishes
            ? new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)
            : null;
        return await connection.CreateChannelAsync(channelOptions, cancellationToken);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        // Once opened, the connection object is kept for good, even while it is down: with automatic
        // recovery it reconnects by itself and re-attaches the worker's consumer. Replacing it when
        // a publish happens to find it mid-recovery would dispose it and strand that consumer (found
        // by the broker-restart test). A publish during the outage simply fails and the caller handles it.
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.Username,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                ClientProvidedName = "aichat-api",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            };
            // If the very first connect fails (broker not up yet) nothing is stored, so the next call tries again.
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _gate.Dispose();
    }
}
