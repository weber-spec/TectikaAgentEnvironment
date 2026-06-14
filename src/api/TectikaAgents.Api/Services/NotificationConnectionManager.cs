using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Manages global SSE connections for the notification stream.
/// Unlike SseConnectionManager (which is scoped per-run), this broadcasts to all connected clients.
/// </summary>
public class NotificationConnectionManager
{
    private readonly List<SseClient> _clients = new();
    private readonly Lock _lock = new();
    private readonly ILogger<NotificationConnectionManager> _logger;

    public NotificationConnectionManager(ILogger<NotificationConnectionManager> logger) => _logger = logger;

    public void AddClient(SseClient client)
    {
        lock (_lock) _clients.Add(client);
        _logger.LogInformation("[NotificationSse] client connected total={Count}", _clients.Count);
    }

    public void RemoveClient(SseClient client)
    {
        lock (_lock) _clients.Remove(client);
        _logger.LogInformation("[NotificationSse] client disconnected total={Count}", _clients.Count);
    }

    public async Task BroadcastAsync(NotificationDocument notification, CancellationToken ct = default)
    {
        List<SseClient> snapshot;
        lock (_lock) snapshot = [.. _clients];

        var json = JsonSerializer.Serialize(notification);
        var data = $"data: {json}\n\n";

        var deadClients = new List<SseClient>();

        foreach (var client in snapshot)
        {
            try
            {
                await client.Writer.WriteAsync(data.AsMemory(), ct);
                await client.Writer.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NotificationSse] client write failed");
                deadClients.Add(client);
            }
        }

        if (deadClients.Count > 0)
        {
            lock (_lock)
                foreach (var dead in deadClients)
                    _clients.Remove(dead);
        }

        _logger.LogDebug("[NotificationSse] broadcast type={Type} to {Count} clients", notification.Type, snapshot.Count);
    }
}
