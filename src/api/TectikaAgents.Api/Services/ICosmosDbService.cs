using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Data-access contract for the platform's persisted entities. Implemented by
/// <see cref="CosmosDbService"/> (real Cosmos DB) and <see cref="InMemoryCosmosDbService"/>
/// (toggleable in-memory mock — see the "MockDatabase" config section).
/// </summary>
public interface ICosmosDbService
{
    // ── Bootstrap ──────────────────────────────────────────────────────────────
    Task EnsureInfrastructureAsync();

    // ── Boards ─────────────────────────────────────────────────────────────────
    Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default);
    Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default);
    Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default);
    Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default);
    Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default);

    // ── Tasks ──────────────────────────────────────────────────────────────────
    Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default);
    Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default);
    Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default);
    Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default);
    Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default);

    // ── Agent Roles ────────────────────────────────────────────────────────────
    Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default);
    Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default);

    // ── Workflow Runs ──────────────────────────────────────────────────────────
    Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default);
    Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default);
    Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default);

    // ── Artifacts ──────────────────────────────────────────────────────────────
    Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default);
    Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default);

    // ── Approvals ──────────────────────────────────────────────────────────────
    Task<Approval> CreateApprovalAsync(Approval approval, CancellationToken ct = default);
    Task<Approval?> GetApprovalAsync(string runId, string approvalId, CancellationToken ct = default);
    Task<Approval> UpdateApprovalAsync(Approval approval, CancellationToken ct = default);
    Task<IEnumerable<Approval>> GetPendingApprovalsAsync(string tenantId, CancellationToken ct = default);

    // ── Audit Log ──────────────────────────────────────────────────────────────
    Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default);

    // ── Edges ──────────────────────────────────────────────────────────────────
    Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default);
    Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default);
    Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default);
    Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default);
    Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default);
    Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default);
}
