using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

// Minimal ICosmosDbService fake that persists board + tenant-connection mutations — used by the board
// integration controller tests (connection registry + per-board bindings + email resolution round-trips).
internal sealed class FakeCosmosForBoard : ICosmosDbService
{
    private readonly ConcurrentDictionary<string, Board> _boards = new();
    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    public Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(board.Id)) board.Id = Guid.NewGuid().ToString();
        _boards[board.Id] = board;
        return Task.FromResult(board);
    }

    public Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default) =>
        Task.FromResult(_boards.TryGetValue(boardId, out var b) && b.TenantId == tenantId ? b : null);

    public Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default)
    {
        _boards[board.Id] = board;
        return Task.FromResult(board);
    }

    // ── Connections (tenant-level registry) ──────────────────────────────────────
    public Task<IEnumerable<Connection>> GetConnectionsAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_connections.Values.Where(c => c.TenantId == tenantId));

    public Task<Connection?> GetConnectionAsync(string tenantId, string connectionId, CancellationToken ct = default) =>
        Task.FromResult(_connections.TryGetValue(connectionId, out var c) && c.TenantId == tenantId ? c : null);

    public Task<Connection> UpsertConnectionAsync(Connection connection, CancellationToken ct = default)
    {
        _connections[connection.Id] = connection;
        return Task.FromResult(connection);
    }

    public Task DeleteConnectionAsync(string tenantId, string connectionId, CancellationToken ct = default)
    {
        _connections.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }

    // ── Everything below is unused in these tests ────────────────────────────
    public Task EnsureInfrastructureAsync() => throw new NotImplementedException();
    public Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask?> TryClaimTaskForRunAsync(string boardId, string taskId, string runId, string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PurgeTaskWorkDataAsync(string tenantId, string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUsageRollupAsync(UsageRollup rollup, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUsageEventAsync(UsageEvent ev, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UsageEventsPage> GetUsageEventsForTaskAsync(string tenantId, string taskId, int max, string? continuationToken, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UsageTimePoint>> GetUsageTimeSeriesAsync(string scope, string scopeId, int days, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<AgentUsage>> GetUsageByAgentAsync(string scope, string scopeId, int days, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunEvent> UpdateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskComment> CreateCommentAsync(TaskComment comment, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskComment?> GetCommentAsync(string taskId, string commentId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskComment> UpsertCommentAsync(TaskComment comment, CancellationToken ct = default) => throw new NotImplementedException();
}
