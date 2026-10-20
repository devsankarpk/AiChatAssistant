namespace AiChatAssistant.Api.Services;

public interface IDirectDeliveryService
{
    /// <summary>
    /// A user just connected: mark every message sent to them that is still undelivered as delivered,
    /// and tell each sender. Idempotent: a second connection finds nothing left to do.
    /// </summary>
    Task MarkPendingDeliveredAsync(int userId, CancellationToken cancellationToken = default);
}
