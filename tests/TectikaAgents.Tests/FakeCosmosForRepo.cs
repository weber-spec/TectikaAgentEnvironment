using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

/// <summary>Shared fake ICosmosDbService for RepoController tests.</summary>
public sealed class FakeCosmosForRepo : ICosmosDbService
{
    private readonly Board? _board;
    public FakeCosmosForRepo(Board? board) => _board = board;

    // ── Bootstrap ──────────────────────────────────────────────────────────
    public Task EnsureInfrastructureAsync() => throw new NotImplementedException();

    // ── Boards ─────────────────────────────────────────────────────────────
    public Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default)
        => Task.FromResult(_board is not null && _board.TenantId == tenantId ? _board : null);
    public Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Tasks ──────────────────────────────────────────────────────────────
    public Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask?> TryClaimTaskForRunAsync(string boardId, string taskId, string runId, string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Agent Roles ────────────────────────────────────────────────────────
    public Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Workflow Runs ──────────────────────────────────────────────────────
    public Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Artifacts ──────────────────────────────────────────────────────────
    public Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Human Interactions ─────────────────────────────────────────────────
    public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Edges ──────────────────────────────────────────────────────────────
    public Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Run trace ──────────────────────────────────────────────────────────
    public Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunEvent> UpdateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Usage tracking (added on main) ───────────────────────────────────────
    public Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUsageRollupAsync(UsageRollup rollup, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUsageEventAsync(UsageEvent ev, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UsageEventsPage> GetUsageEventsForTaskAsync(string tenantId, string taskId, int max, string? continuationToken, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UsageTimePoint>> GetUsageTimeSeriesAsync(string scope, string scopeId, int days, CancellationToken ct = default) => throw new NotImplementedException();

    // ── Preview Sessions ─────────────────────────────────────────────────────
    public Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default) => throw new NotImplementedException();
}
