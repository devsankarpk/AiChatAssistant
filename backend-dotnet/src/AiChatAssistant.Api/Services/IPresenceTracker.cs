namespace AiChatAssistant.Api.Services;

/// <summary>
/// Who has at least one live realtime connection right now. A user can have several (one per
/// browser tab), so "online" means "one or more connections" and "offline" means the last one
/// closed. The seam exists so controllers and tests never need a real hub.
/// </summary>
public interface IPresenceTracker
{
    bool IsOnline(int userId);

    /// <returns>true when this is the user's first connection, i.e. they just came online.</returns>
    bool UserConnected(int userId, string connectionId);

    /// <returns>true when that was the user's last connection, i.e. they just went offline.</returns>
    bool UserDisconnected(int userId, string connectionId);
}
