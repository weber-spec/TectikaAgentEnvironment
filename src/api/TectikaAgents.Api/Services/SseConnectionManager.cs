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
        var clients = _connections.AddOrUpdate(runId,
            _ => [client],
            (_, existing) => { existing.Add(client); return existing; });
        _logger.LogInformation("[Sse] client connected channel={Channel} total={Count}", runId, clients.Count);
    }

    public void RemoveClient(string runId, SseClient client)
    {
        if (_connections.TryGetValue(runId, out var clients))
        {
            clients.Remove(client);
            _logger.LogInformation("[Sse] client disconnected channel={Channel} total={Count}", runId, clients.Count);
        }
    }

    public async Task BroadcastAsync(AgentEvent agentEvent, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(agentEvent.RunId, out var clients) || clients.Count == 0)
            return;

        _logger.LogDebug("[Sse] broadcast event={Event} to {Count} clients on channel={Channel}", agentEvent.Type, clients.Count, agentEvent.RunId);

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
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
            {
                // Expected at run handoff / tab close: the client's response body is gone. Drop it quietly
                // rather than logging a warning on every disconnect race (QA S3 §4.1).
                _logger.LogDebug("[Sse] client gone channel={Channel}", agentEvent.RunId);
                deadClients.Add(client);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sse] client write failed channel={Channel}", agentEvent.RunId);
                deadClients.Add(client);
            }
        }

        foreach (var dead in deadClients)
            clients.Remove(dead);
    }
}

public record SseClient(TextWriter Writer, CancellationToken CancellationToken);
