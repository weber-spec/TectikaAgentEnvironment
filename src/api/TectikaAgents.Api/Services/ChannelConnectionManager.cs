using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Per-channel SSE fan-out — a façade over <see cref="SseHub"/> keyed by channelId. Best-effort:
/// persistence is the source of truth and the client's poll is the backstop, so a failed write never
/// aborts a post.
/// </summary>
public class ChannelConnectionManager
{
    private readonly SseHub _hub;
    private readonly ILogger<ChannelConnectionManager> _logger;

    public ChannelConnectionManager(SseHub hub, ILogger<ChannelConnectionManager> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public void AddClient(string channelId, SseClient client) => _hub.Add(SseKeys.Channel(channelId), client);
    public void RemoveClient(string channelId, SseClient client) => _hub.Remove(SseKeys.Channel(channelId), client);

    public async Task BroadcastAsync(string channelId, ChannelMessage message, CancellationToken ct = default)
    {
        var frame = $"data: {JsonSerializer.Serialize(message)}\n\n";
        _logger.LogDebug("[ChannelSse] broadcast kind={Kind} channel={Channel}", message.Kind, channelId);
        await _hub.BroadcastAsync(frame, ct, SseKeys.Channel(channelId));
    }
}
