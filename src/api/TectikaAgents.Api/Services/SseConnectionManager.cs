using System.Collections.Concurrent;
using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// מנהל SSE connections — כל run יכול להיות מנוטר על ידי מספר clients.
/// </summary>
public class SseConnectionManager
{
    private readonly ConcurrentDictionary<string, List<SseClient>> _connections = new();
    private readonly ILogger<SseConnectionManager> _logger;

    public SseConnectionManager(ILogger<SseConnectionManager> logger) => _logger = logger;

    public void AddClient(string runId, SseClient client)
    {
        _connections.AddOrUpdate(runId,
            _ => [client],
            (_, existing) => { existing.Add(client); return existing; });
    }

    public void RemoveClient(string runId, SseClient client)
    {
        if (_connections.TryGetValue(runId, out var clients))
            clients.Remove(client);
    }

    public async Task BroadcastAsync(AgentEvent agentEvent, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(agentEvent.RunId, out var clients) || clients.Count == 0)
            return;

        var json = JsonSerializer.Serialize(agentEvent);
        var data = $"data: {json}\n\n";

        var deadClients = new List<SseClient>();

        foreach (var client in clients.ToList())
        {
            try
            {
                await client.Writer.WriteAsync(data.AsMemory(), ct);
                await client.Writer.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE client disconnected for run {RunId}", agentEvent.RunId);
                deadClients.Add(client);
            }
        }

        foreach (var dead in deadClients)
            clients.Remove(dead);
    }
}

public record SseClient(TextWriter Writer, CancellationToken CancellationToken);
