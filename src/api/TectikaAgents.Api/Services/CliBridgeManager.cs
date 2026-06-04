using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// מנהל WebSocket connections מ-CLI agents חיצוניים (Claude Code, Cursor).
/// כל task יכול להיות מחובר ל-CLI אחד בכל עת.
/// </summary>
public class CliBridgeManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly SseConnectionManager _sse;
    private readonly ICosmosDbService _cosmos;
    private readonly ILogger<CliBridgeManager> _logger;

    public CliBridgeManager(SseConnectionManager sse, ICosmosDbService cosmos, ILogger<CliBridgeManager> logger)
    {
        _sse = sse;
        _cosmos = cosmos;
        _logger = logger;
    }

    public async Task HandleConnectionAsync(string taskId, string runId, string tenantId, WebSocket ws, CancellationToken ct)
    {
        _connections[taskId] = ws;
        _logger.LogInformation("CLI agent connected for task {TaskId}", taskId);

        await _sse.BroadcastAsync(new AgentEvent
        {
            Type = AgentEvent.Types.CliConnected,
            RunId = runId,
            TaskId = taskId,
            Content = "CLI agent connected"
        }, ct);

        try
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await ProcessCliMessageAsync(taskId, runId, tenantId, message, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI WebSocket error for task {TaskId}", taskId);
        }
        finally
        {
            _connections.TryRemove(taskId, out _);
            await _sse.BroadcastAsync(new AgentEvent
            {
                Type = AgentEvent.Types.CliDisconnected,
                RunId = runId,
                TaskId = taskId
            }, ct);
        }
    }

    private async Task ProcessCliMessageAsync(string taskId, string runId, string tenantId, string message, CancellationToken ct)
    {
        var cliMessage = JsonSerializer.Deserialize<CliMessage>(message);
        if (cliMessage is null) return;

        // Broadcast stdout/stderr to SSE viewers
        await _sse.BroadcastAsync(new AgentEvent
        {
            Type = AgentEvent.Types.CliOutput,
            RunId = runId,
            TaskId = taskId,
            Content = cliMessage.Content
        }, ct);

        // If the CLI sends an artifact update, persist it
        if (cliMessage.Type == "artifact" && !string.IsNullOrEmpty(cliMessage.Content))
        {
            var artifact = new Artifact
            {
                TaskId = taskId,
                RunId = runId,
                TenantId = tenantId,
                Content = cliMessage.Content,
                ContentType = ArtifactContentType.Code,
                Origin = ArtifactOrigin.CliBridge,
                InternalLogs = [$"CLI update at {DateTimeOffset.UtcNow:u}"]
            };

            var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
            await _sse.BroadcastAsync(new AgentEvent
            {
                Type = AgentEvent.Types.ArtifactUpdated,
                RunId = runId,
                TaskId = taskId,
                ArtifactId = saved.Id
            }, ct);
        }
    }

    public bool IsConnected(string taskId) => _connections.ContainsKey(taskId);
}

public record CliMessage(string Type, string Content, Dictionary<string, string>? Meta = null);
