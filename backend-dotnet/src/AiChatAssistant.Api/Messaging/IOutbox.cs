using AiChatAssistant.Api.Data;

namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Records an event to be published once the current change is saved. The event is stored in the same database
/// transaction as the change (see <see cref="AppDbContext.SaveChangesAsync(bool, CancellationToken)"/>), so it
/// exists if and only if the change does. The payload is built after the first save, when generated ids exist.
/// </summary>
public interface IOutbox
{
    void Add(string type, Func<object> payload);
}

public sealed class OutboxWriter : IOutbox
{
    private readonly AppDbContext _db;

    public OutboxWriter(AppDbContext db)
    {
        _db = db;
    }

    public void Add(string type, Func<object> payload) => _db.QueueEvent(type, payload);
}

/// <summary>Used when there is no broker (or the outbox is switched off): nothing is recorded.</summary>
public sealed class NoOpOutbox : IOutbox
{
    public void Add(string type, Func<object> payload)
    {
    }
}
