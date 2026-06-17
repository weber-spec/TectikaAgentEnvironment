using System.Collections.Concurrent;
using TectikaAgents.Api.Services.MockData;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Services;

/// <summary>
/// In-memory stand-in for <see cref="CosmosDbService"/>, used while no Azure Cosmos DB
/// account is provisioned. Enabled via the "MockDatabase:Enabled" config flag.
///
/// State lives in process memory only — it is re-seeded on every startup and lost on restart.
/// Each store mirrors the partitioning/query semantics the controllers rely on so the swap is
/// transparent: <c>Get*</c>-by-id returns <c>null</c> when absent (matching Cosmos' NotFound→null),
/// and the list methods filter exactly like the corresponding SQL <c>WHERE</c> clauses.
/// </summary>
public class InMemoryCosmosDbService : ICosmosDbService
{
    private readonly ConcurrentDictionary<string, Board> _boards = new();
    private readonly ConcurrentDictionary<string, TaskEdge> _edges = new();
    private readonly ConcurrentDictionary<string, AgentTask> _tasks = new();
    private readonly ConcurrentDictionary<string, AgentRole> _agentRoles = new();
    private readonly ConcurrentDictionary<string, WorkflowRun> _runs = new();
    private readonly ConcurrentDictionary<string, Artifact> _artifacts = new();
    private readonly ConcurrentDictionary<string, Approval> _approvals = new();
    private readonly ConcurrentDictionary<string, HumanInteraction> _interactions = new();
    private readonly ConcurrentDictionary<string, AuditEntry> _audit = new();

    private readonly ILogger<InMemoryCosmosDbService> _logger;

    public InMemoryCosmosDbService(ILogger<InMemoryCosmosDbService> logger)
    {
        _logger = logger;
        MockDataSeeder.Seed(_boards, _tasks, _agentRoles, _runs, _artifacts, _approvals, _edges);
        _logger.LogWarning(
            "MockDatabase enabled — serving {Boards} boards, {Tasks} tasks, {Roles} agent roles, " +
            "{Runs} runs, {Artifacts} artifacts, {Approvals} approvals, {Edges} edges from in-memory store (no Cosmos DB).",
            _boards.Count, _tasks.Count, _agentRoles.Count, _runs.Count, _artifacts.Count, _approvals.Count, _edges.Count);
    }

    // ── Bootstrap ──────────────────────────────────────────────────────────────
    public Task EnsureInfrastructureAsync() => Task.CompletedTask; // nothing to provision

    // ── Boards ─────────────────────────────────────────────────────────────────
    public Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default)
    {
        _boards[board.Id] = board;
        return Task.FromResult(board);
    }

    public Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_boards.Values.Where(b => b.TenantId == tenantId).AsEnumerable());

    public Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default) =>
        Task.FromResult(_boards.TryGetValue(boardId, out var b) && b.TenantId == tenantId ? b : null);

    public Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default)
    {
        _boards[board.Id] = board;
        return Task.FromResult(board);
    }

    public Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default)
    {
        foreach (var key in _tasks.Keys.Where(k => _tasks.TryGetValue(k, out var t) && t.BoardId == boardId).ToList())
            _tasks.TryRemove(key, out _);
        _boards.TryRemove(boardId, out _);
        return Task.CompletedTask;
    }

    // ── Tasks ──────────────────────────────────────────────────────────────────
    public Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default)
    {
        _tasks[task.Id] = task;
        return Task.FromResult(task);
    }

    public Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default) =>
        Task.FromResult(_tasks.TryGetValue(taskId, out var t) && t.BoardId == boardId ? t : null);

    public Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default) =>
        Task.FromResult(_tasks.Values.Where(t => t.BoardId == boardId).AsEnumerable());

    public Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default)
    {
        _tasks[task.Id] = task;
        return Task.FromResult(task);
    }

    public Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(taskId, out var t) && t.BoardId == boardId)
            _tasks.TryRemove(taskId, out _);
        return Task.CompletedTask;
    }

    // ── Agent Roles ────────────────────────────────────────────────────────────
    public Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_agentRoles.Values.Where(r => r.TenantId == tenantId).AsEnumerable());

    public Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default)
    {
        _agentRoles[role.Id] = role;
        return Task.FromResult(role);
    }

    public Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
        => Task.FromResult(_agentRoles.TryGetValue(roleId, out var r) && r.TenantId == tenantId ? r : null);

    public Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
    {
        if (_agentRoles.TryGetValue(roleId, out var r) && r.TenantId == tenantId)
            _agentRoles.TryRemove(roleId, out _);
        return Task.CompletedTask;
    }

    // ── Workflow Runs ──────────────────────────────────────────────────────────
    public Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.FromResult(run);
    }

    public Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default) =>
        Task.FromResult(_runs.TryGetValue(runId, out var r) && r.TaskId == taskId ? r : null);

    public Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(_runs.Values.Where(r => r.TaskId == taskId).AsEnumerable());

    public Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.FromResult(run);
    }

    // ── Artifacts ──────────────────────────────────────────────────────────────
    public Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default)
    {
        _artifacts[artifact.Id] = artifact;
        return Task.FromResult(artifact);
    }

    public Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(_artifacts.Values
            .Where(a => a.TaskId == taskId)
            .OrderByDescending(a => a.Version)
            .AsEnumerable());

    // ── Approvals ──────────────────────────────────────────────────────────────
    public Task<Approval> CreateApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        _approvals[approval.Id] = approval;
        return Task.FromResult(approval);
    }

    public Task<Approval?> GetApprovalAsync(string runId, string approvalId, CancellationToken ct = default) =>
        Task.FromResult(_approvals.TryGetValue(approvalId, out var a) && a.RunId == runId ? a : null);

    public Task<Approval> UpdateApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        _approvals[approval.Id] = approval;
        return Task.FromResult(approval);
    }

    public Task<IEnumerable<Approval>> GetPendingApprovalsAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_approvals.Values
            .Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending)
            .AsEnumerable());

    // ── Human Interactions ──────────────────────────────────────────────────────
    public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        _interactions[interaction.Id] = interaction;
        return Task.FromResult(interaction);
    }

    public Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default) =>
        Task.FromResult(_interactions.TryGetValue(interactionId, out var i) && i.RunId == runId ? i : null);

    public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        _interactions[interaction.Id] = interaction;
        return Task.FromResult(interaction);
    }

    public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_interactions.Values
            .Where(i => i.TenantId == tenantId && i.Status == InteractionStatus.Pending)
            .AsEnumerable());

    // ── Audit Log ──────────────────────────────────────────────────────────────
    public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _audit[entry.Id] = entry;
        return Task.CompletedTask;
    }

    // ── Edges ──────────────────────────────────────────────────────────────────
    public Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
    { _edges[edge.Id] = edge; return Task.FromResult(edge); }

    public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default)
    => Task.FromResult(_edges.Values.Where(e => e.BoardId == boardId).AsEnumerable());

    public Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
    => Task.FromResult(_edges.TryGetValue(edgeId, out var e) && e.BoardId == boardId ? e : null);

    public Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
    { edge.UpdatedAt = DateTimeOffset.UtcNow; _edges[edge.Id] = edge; return Task.FromResult(edge); }

    public Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
    {
        if (_edges.TryGetValue(edgeId, out var e) && e.BoardId == boardId)
            _edges.TryRemove(edgeId, out _);
        return Task.CompletedTask;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunEvent> _runEvents = new();
    public Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<RunEvent>)_runEvents.Values
            .Where(e => e.TaskId == taskId && (sinceRound is null || e.Round >= sinceRound))
            .OrderBy(e => e.Timestamp).ToList());
    public Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default)
    { _runEvents[e.Id] = e; return Task.FromResult(e); }

    public Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        foreach (var e in _edges.Values
            .Where(e => e.BoardId == boardId && (e.SourceTaskId == taskId || e.TargetTaskId == taskId))
            .ToList())
        {
            _edges.TryRemove(e.Id, out _);
        }
        return Task.CompletedTask;
    }

    // ── Usage ──────────────────────────────────────────────────────────────────

    // Keyed by rollup id (e.g. "task:<taskId>", "board:<boardId>", "project:<tenantId>").
    public readonly ConcurrentDictionary<string, UsageRollup> _usageRollups = new();
    // Keyed by event id.
    public readonly ConcurrentDictionary<string, UsageEvent> _usageEvents = new();

    /// <summary>Called by seeders (e.g. Task 17) to pre-populate a rollup in mock mode.</summary>
    public void AddUsageRollup(UsageRollup rollup) => _usageRollups[rollup.Id] = rollup;

    /// <summary>Called by seeders (e.g. Task 17) to pre-populate a usage event in mock mode.</summary>
    public void AddUsageEvent(UsageEvent evt) => _usageEvents[evt.Id] = evt;

    public Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default)
    {
        var id = UsageRollup.TaskId(taskId);
        if (_usageRollups.TryGetValue(id, out var rollup) && rollup.TenantId == tenantId)
        {
            rollup.CurrentSession = new SessionBucket { SessionId = newSessionId, Since = DateTimeOffset.UtcNow };
            rollup.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_usageRollups.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null);

    public Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_usageRollups.Values.Where(r => r.TenantId == tenantId).ToList());

    public Task UpsertUsageRollupAsync(UsageRollup rollup, CancellationToken ct = default)
    {
        _usageRollups[rollup.Id] = rollup;
        return Task.CompletedTask;
    }

    public Task<UsageEventsPage> GetUsageEventsForTaskAsync(string tenantId, string taskId, int max, string? continuationToken, CancellationToken ct = default)
    {
        var items = _usageEvents.Values
            .Where(e => e.TaskId == taskId && e.TenantId == tenantId)
            .OrderByDescending(e => e.Timestamp)
            .Take(max)
            .ToList();
        return Task.FromResult(new UsageEventsPage { Items = items, ContinuationToken = null });
    }
}
