using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Data-access contract for the platform's persisted entities. Implemented by
/// <see cref="CosmosDbService"/> (real Cosmos DB) and <see cref="InMemoryCosmosDbService"/>
/// (toggleable in-memory mock — see the "MockDatabase" config section).
///
/// Extends <see cref="ITaskGraphReader"/> so the API can hand itself to
/// <see cref="Core.Scheduling.TaskStartGate"/> — the shared "may this task start?" rule —
/// without a second abstraction. That contributes GetTaskAsync and the Get{Up,Down}streamTaskIds pair.
/// </summary>
public interface ICosmosDbService : ITaskGraphReader
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
    // GetTaskAsync comes from ITaskGraphReader.
    Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default);
    Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default);
    /// <summary>Atomically transition a task Backlog→InProgress and link the run, only if it is still
    /// Backlog. Returns the updated task on success, or null if the task is gone, no longer Backlog, or
    /// another caller won the race. The single guard against the same task starting two concurrent runs.</summary>
    Task<AgentTask?> TryClaimTaskForRunAsync(string boardId, string taskId, string runId, string sessionId, CancellationToken ct = default);
    Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default);
    /// <summary>Delete ALL produced work for one task — runs (and their human interactions),
    /// artifacts, run events, usage events, and the task usage rollup. Best-effort and idempotent;
    /// used by board reset. Does NOT delete the task document itself.</summary>
    Task PurgeTaskWorkDataAsync(string tenantId, string boardId, string taskId, CancellationToken ct = default);

    // ── Connections (tenant-level registry) ──────────────────────────────────────
    Task<IEnumerable<Connection>> GetConnectionsAsync(string tenantId, CancellationToken ct = default);
    Task<Connection?> GetConnectionAsync(string tenantId, string connectionId, CancellationToken ct = default);
    Task<Connection> UpsertConnectionAsync(Connection connection, CancellationToken ct = default);
    Task DeleteConnectionAsync(string tenantId, string connectionId, CancellationToken ct = default);

    // ── Tool policy (tenant-level global tool enable/disable) ────────────────────
    Task<ToolPolicy?> GetToolPolicyAsync(string tenantId, CancellationToken ct = default);
    Task<ToolPolicy> UpsertToolPolicyAsync(ToolPolicy policy, CancellationToken ct = default);

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
    /// <summary>Ledger-truth usage aggregated per agent role over the last <paramref name="days"/> days. scope = "board" (scopeId=boardId) or "project" (scopeId=tenantId).</summary>
    Task<List<AgentUsage>> GetUsageByAgentAsync(string scope, string scopeId, int days, CancellationToken ct = default);

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

    // ── Task comments ────────────────────────────────────────────────────────────
    Task<TaskComment> CreateCommentAsync(TaskComment comment, CancellationToken ct = default);
    Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string taskId, CancellationToken ct = default);
    Task<TaskComment?> GetCommentAsync(string taskId, string commentId, CancellationToken ct = default);
    Task<TaskComment> UpsertCommentAsync(TaskComment comment, CancellationToken ct = default);

    // ── Channels ───────────────────────────────────────────────────────────────
    Task<Channel> CreateChannelAsync(Channel channel, CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetChannelsByTenantAsync(string tenantId, CancellationToken ct = default);
    Task<Channel?> GetChannelAsync(string tenantId, string channelId, CancellationToken ct = default);
    Task<Channel> UpsertChannelAsync(Channel channel, CancellationToken ct = default);
    /// <summary>Channels bound to a board (usually 0 or 1 — the auto-created board channel).</summary>
    Task<IReadOnlyList<Channel>> GetChannelsForBoardAsync(string tenantId, string boardId, CancellationToken ct = default);

    // ── Channel messages ───────────────────────────────────────────────────────
    Task<ChannelMessage> CreateChannelMessageAsync(ChannelMessage message, CancellationToken ct = default);
    /// <summary>Messages in a channel oldest-first; optionally only those created after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<ChannelMessage>> GetChannelMessagesAsync(string channelId, DateTimeOffset? since = null, CancellationToken ct = default);
    Task<ChannelMessage?> GetChannelMessageAsync(string channelId, string messageId, CancellationToken ct = default);
    Task<ChannelMessage> UpsertChannelMessageAsync(ChannelMessage message, CancellationToken ct = default);
}
