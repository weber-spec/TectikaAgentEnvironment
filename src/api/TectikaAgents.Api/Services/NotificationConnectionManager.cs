using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Global notification SSE fan-out — a façade over <see cref="SseHub"/> on a single key. Unlike the run
/// and channel streams this is not scoped: every connected client gets every notification.
/// </summary>
public class NotificationConnectionManager
{
    private readonly SseHub _hub;
    private readonly ILogger<NotificationConnectionManager> _logger;

    public NotificationConnectionManager(SseHub hub, ILogger<NotificationConnectionManager> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public void AddClient(SseClient client) => _hub.Add(SseKeys.Notifications, client);
    public void RemoveClient(SseClient client) => _hub.Remove(SseKeys.Notifications, client);

    public async Task BroadcastAsync(NotificationDocument notification, CancellationToken ct = default)
    {
        var frame = $"data: {JsonSerializer.Serialize(notification)}\n\n";
        _logger.LogDebug("[NotificationSse] broadcast type={Type}", notification.Type);
        await _hub.BroadcastAsync(frame, ct, SseKeys.Notifications);
    }
}
