using System.Collections.Concurrent;
using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Manages per-channel SSE connections — each channel can be watched by multiple clients.
/// Keyed by channelId (like SseConnectionManager is keyed by runId), broadcasts a ChannelMessage
/// to every client subscribed to that channel. Best-effort: persistence is the source of truth and
/// the client's poll is the backstop, so a failed write never aborts a post.
/// </summary>
public class ChannelConnectionManager
{
    private readonly ConcurrentDictionary<string, List<SseClient>> _connections = new();
    private readonly ILogger<ChannelConnectionManager> _logger;

    public ChannelConnectionManager(ILogger<ChannelConnectionManager> logger) => _logger = logger;

    public void AddClient(string channelId, SseClient client)
    {
        var clients = _connections.AddOrUpdate(channelId,
            _ => [client],
            (_, existing) => { existing.Add(client); return existing; });
        _logger.LogInformation("[ChannelSse] client connected channel={Channel} total={Count}", channelId, clients.Count);
    }

    public void RemoveClient(string channelId, SseClient client)
    {
        if (_connections.TryGetValue(channelId, out var clients))
        {
            clients.Remove(client);
            _logger.LogInformation("[ChannelSse] client disconnected channel={Channel} total={Count}", channelId, clients.Count);
        }
    }

    public async Task BroadcastAsync(string channelId, ChannelMessage message, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(channelId, out var clients) || clients.Count == 0)
            return;

        var json = JsonSerializer.Serialize(message);
        var data = $"data: {json}\n\n";

        var deadClients = new List<SseClient>();

        foreach (var client in clients.ToList())
        {
            try
            {
                await client.Writer.WriteAsync(data.AsMemory(), ct);
                await client.Writer.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
            {
                _logger.LogDebug("[ChannelSse] client gone channel={Channel}", channelId);
                deadClients.Add(client);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ChannelSse] client write failed channel={Channel}", channelId);
                deadClients.Add(client);
            }
        }

        foreach (var dead in deadClients)
            clients.Remove(dead);

        _logger.LogDebug("[ChannelSse] broadcast kind={Kind} to {Count} clients on channel={Channel}", message.Kind, clients.Count, channelId);
    }
}
