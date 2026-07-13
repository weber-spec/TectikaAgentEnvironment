using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Run-event fan-out. Every event goes to two key-spaces at once: the run's own stream
/// (<c>/api/runs/{runId}/stream</c>, kept for the CLI and for compatibility) and the multiplexed board
/// stream (<c>/api/boards/{boardId}/stream</c>) that the web app subscribes to exactly once per board.
///
/// The board page used to open one EventSource per task-with-a-run, which blew past the browser's ~6
/// connections-per-origin cap on HTTP/1.1 and left every subsequent fetch hanging forever. This is the
/// server half of that fix — see <see cref="SseHub"/> and <see cref="IRunBoardResolver"/>.
/// </summary>
public class SseConnectionManager
{
    private readonly SseHub _hub;
    private readonly IRunBoardResolver _resolver;
    private readonly ILogger<SseConnectionManager> _logger;

    public SseConnectionManager(SseHub hub, IRunBoardResolver resolver, ILogger<SseConnectionManager> logger)
    {
        _hub = hub;
        _resolver = resolver;
        _logger = logger;
    }

    public void AddClient(string runId, SseClient client) => _hub.Add(SseKeys.Run(runId), client);
    public void RemoveClient(string runId, SseClient client) => _hub.Remove(SseKeys.Run(runId), client);

    /// <summary>Seed the run→board map. Called wherever a run is persisted, so the resolver's Cosmos
    /// fallback stays the exception rather than the rule.</summary>
    public void Remember(string runId, string? taskId, string boardId) => _resolver.Remember(runId, taskId, boardId);

    public async Task BroadcastAsync(AgentEvent agentEvent, CancellationToken ct = default)
    {
        var boardId = await _resolver.ResolveBoardIdAsync(agentEvent.RunId, agentEvent.TaskId, ct);

        var json = JsonSerializer.Serialize(agentEvent);
        var frame = $"data: {json}\n\n";

        _logger.LogDebug("[Sse] broadcast event={Event} run={RunId} board={BoardId}",
            agentEvent.Type, agentEvent.RunId, boardId ?? "-");

        await _hub.BroadcastAsync(frame, ct,
            SseKeys.Run(agentEvent.RunId),
            boardId is null ? null : SseKeys.Board(boardId));
    }
}
