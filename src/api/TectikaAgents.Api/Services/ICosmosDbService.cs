using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

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
    /// <summary>Atomically transition a task Backlog→InProgress and link the run, only if it is still
    /// Backlog. Returns the updated task on success, or null if the task is gone, no longer Backlog, or
    /// another caller won the race. The single guard against the same task starting two concurrent runs.</summary>
    Task<AgentTask?> TryClaimTaskForRunAsync(string boardId, string taskId, string runId, string sessionId, CancellationToken ct = default);
    Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default);

    // ── Agent Roles ────────────────────────────────────────────────────────────
    Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default);
    Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default);
    Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default);
    Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default);

    // ── Workflow Runs ──────────────────────────────────────────────────────────
    Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default);
    Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default);
    Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string taskId, CancellationToken ct = default);
    Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default);

    // ── Artifacts ──────────────────────────────────────────────────────────────
    Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default);
    Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default);

    // ── Human Interactions ─────────────────────────────────────────────────────
    Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default);
    Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default);
    Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default);
    Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default);


    // ── Edges ──────────────────────────────────────────────────────────────────
    Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default);
    Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default);
    Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default);
    Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default);
    Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default);

    // ── Usage ─────────────────────────────────────────────────────────────────
    Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default);
    Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default);
    Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default);
    Task UpsertUsageRollupAsync(UsageRollup rollup, CancellationToken ct = default);
    Task UpsertUsageEventAsync(UsageEvent ev, CancellationToken ct = default);
    Task<UsageEventsPage> GetUsageEventsForTaskAsync(string tenantId, string taskId, int max, string? continuationToken, CancellationToken ct = default);
    /// <summary>Daily token/cost series for the last <paramref name="days"/> days. scope = "board" (scopeId=boardId) or "project" (scopeId=tenantId). Days with no usage are returned as zero points so the chart is continuous.</summary>
    Task<List<UsageTimePoint>> GetUsageTimeSeriesAsync(string scope, string scopeId, int days, CancellationToken ct = default);

    // ── Run trace ────────────────────────────────────────────────────────────────
    /// <summary>Steerable run trace for a task (Activity tab replay), ordered oldest-first.</summary>
    Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default);
    Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default);
    /// <summary>Upsert an existing run event (same id) — e.g. patch a control tool's result with the
    /// human's answer once they respond, so the transcript line reflects what was decided.</summary>
    Task<RunEvent> UpdateRunEventAsync(RunEvent e, CancellationToken ct = default);
    Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default);

    // ── Preview Sessions ─────────────────────────────────────────────────────────
    Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default);
    Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default);
    Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default);
}
