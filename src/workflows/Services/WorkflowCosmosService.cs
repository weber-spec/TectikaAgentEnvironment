using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// Slim Cosmos DB access layer לשימוש מתוך Durable Function Activities.
/// </summary>
public class WorkflowCosmosService
{
    private readonly CosmosClient _client;
    private readonly string _db;
    private readonly ILogger<WorkflowCosmosService> _logger;

    public WorkflowCosmosService(CosmosClient client, IOptions<CosmosDbSettings> settings, ILogger<WorkflowCosmosService> logger)
    {
        _client = client;
        _db = settings.Value.DatabaseName;
        _logger = logger;
    }

    // Container name constants — used by this service and tasks in later phases
    public const string UsageEventsContainer = "usageEvents";
    public const string UsageRollupsContainer = "usageRollups";

    private Container C(string name) => _client.GetContainer(_db, name);

    // ── AgentRole ─────────────────────────────────────────────────────────────

    public async Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
    {
        try
        {
            var res = await C("agentRoles").ReadItemAsync<AgentRole>(roleId, new PartitionKey(tenantId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default) =>
        await C("agentRoles").UpsertItemAsync(role, new PartitionKey(role.TenantId), cancellationToken: ct);

    // ── AgentTask ─────────────────────────────────────────────────────────────

    public async Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        _logger.LogDebug("[WorkflowCosmos] read task {TaskId} board={BoardId}", taskId, boardId);
        try
        {
            var res = await C("tasks").ReadItemAsync<AgentTask>(taskId, new PartitionKey(boardId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task UpdateTaskStatusAsync(string boardId, string taskId, AgentTaskStatus status, string? workflowRunId, CancellationToken ct = default)
    {
        _logger.LogDebug("[WorkflowCosmos] update task {TaskId} status={Status}", taskId, status);
        var task = await GetTaskAsync(boardId, taskId, ct);
        if (task is null) return;
        task.Status = status;
        if (workflowRunId is not null) task.WorkflowRunId = workflowRunId;
        await C("tasks").ReplaceItemAsync(task, taskId, new PartitionKey(boardId), cancellationToken: ct);
    }

    // ── Edges ─────────────────────────────────────────────────────────────────

    public async Task<List<string>> GetUpstreamTaskIdsAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var ids = new List<string>();
        var q = new QueryDefinition(
            "SELECT VALUE c.sourceTaskId FROM c WHERE c.boardId=@b AND c.targetTaskId=@t AND c.kind='Dependency'")
            .WithParameter("@b", boardId).WithParameter("@t", taskId);
        var iter = C("taskEdges").GetItemQueryIterator<string>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
        while (iter.HasMoreResults) ids.AddRange(await iter.ReadNextAsync(ct));
        return ids;
    }

    public async Task<List<string>> GetDownstreamTaskIdsAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var ids = new List<string>();
        var q = new QueryDefinition(
            "SELECT VALUE c.targetTaskId FROM c WHERE c.boardId=@b AND c.sourceTaskId=@t AND c.kind='Dependency'")
            .WithParameter("@b", boardId).WithParameter("@t", taskId);
        var iter = C("taskEdges").GetItemQueryIterator<string>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
        while (iter.HasMoreResults) ids.AddRange(await iter.ReadNextAsync(ct));
        return ids;
    }

    public async Task<List<TaskEdge>> GetOutgoingQaFeedbackEdgesAsync(
        string boardId, string taskId, CancellationToken ct = default)
    {
        var edges = new List<TaskEdge>();
        var q = new QueryDefinition(
            "SELECT * FROM c WHERE c.boardId=@b AND c.sourceTaskId=@t AND c.kind='QaFeedback'")
            .WithParameter("@b", boardId).WithParameter("@t", taskId);
        var iter = C("taskEdges").GetItemQueryIterator<TaskEdge>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
        while (iter.HasMoreResults) edges.AddRange(await iter.ReadNextAsync(ct));
        return edges;
    }

    public async Task UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default) =>
        await C("taskEdges").ReplaceItemAsync(edge, edge.Id, new PartitionKey(edge.BoardId), cancellationToken: ct);

    public async Task<bool> HasOutgoingQaFeedbackEdgeAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var q = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c WHERE c.boardId=@b AND c.sourceTaskId=@t AND c.kind='QaFeedback'")
            .WithParameter("@b", boardId).WithParameter("@t", taskId);
        var iter = C("taskEdges").GetItemQueryIterator<int>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            if (page.FirstOrDefault() > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// BFS forward from startTaskId through Dependency edges up to endTaskId (inclusive).
    /// Returns all task IDs in the pipeline segment for reset.
    /// </summary>
    public async Task<List<string>> GetTasksBetweenAsync(
        string boardId, string startTaskId, string endTaskId, CancellationToken ct = default)
    {
        var q = new QueryDefinition(
            "SELECT c.sourceTaskId, c.targetTaskId FROM c WHERE c.boardId=@b AND c.kind='Dependency'")
            .WithParameter("@b", boardId);
        var iter = C("taskEdges").GetItemQueryIterator<TaskEdgeSlim>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });

        var adj = new Dictionary<string, List<string>>();
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            foreach (var e in page)
            {
                if (!adj.TryGetValue(e.SourceTaskId, out var list)) adj[e.SourceTaskId] = list = new();
                list.Add(e.TargetTaskId);
            }
        }

        var visited = new HashSet<string> { startTaskId };
        var queue = new Queue<string>(new[] { startTaskId });
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == endTaskId) break;
            if (!adj.TryGetValue(cur, out var nexts)) continue;
            foreach (var next in nexts) if (visited.Add(next)) queue.Enqueue(next);
        }
        visited.Add(endTaskId);
        return visited.ToList();
    }

    public async Task ResetTasksToBacklogAsync(string boardId, IEnumerable<string> taskIds, CancellationToken ct = default)
    {
        foreach (var taskId in taskIds)
        {
            var task = await GetTaskAsync(boardId, taskId, ct);
            if (task is null) continue;
            task.Status = AgentTaskStatus.Backlog;
            task.WorkflowRunId = null;
            task.TaskBrief = "";
            await C("tasks").ReplaceItemAsync(task, taskId, new PartitionKey(boardId), cancellationToken: ct);
        }
    }

    // ── WorkflowRun ───────────────────────────────────────────────────────────

    public async Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default)
    {
        try
        {
            var res = await C("workflowRuns").ReadItemAsync<WorkflowRun>(runId, new PartitionKey(taskId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task UpdateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        _logger.LogDebug("[WorkflowCosmos] wrote run id={Id} status={Status}", run.Id, run.Status);
        await C("workflowRuns").ReplaceItemAsync(run, run.Id, new PartitionKey(run.TaskId), cancellationToken: ct);
    }

    public async Task PatchRunWorkspaceAsync(string runId, string taskId, string containerName, string? endpoint = null, string? token = null, CancellationToken ct = default)
    {
        var res = await C("workflowRuns").ReadItemAsync<WorkflowRun>(runId, new PartitionKey(taskId), cancellationToken: ct);
        var run = res.Resource;
        if (run is null)
        {
            _logger.LogWarning("[WorkflowCosmos] PatchRunWorkspace: run {RunId} not found", runId);
            return;
        }
        run.WorkspaceContainerName = containerName;
        run.WorkspaceEndpoint = endpoint;
        run.WorkspaceToken = token;
        await C("workflowRuns").ReplaceItemAsync(run, run.Id, new PartitionKey(run.TaskId), cancellationToken: ct);
    }

    public async Task PatchRunBranchAsync(string runId, string branchName, int? pullRequestNumber, CancellationToken ct = default)
    {
        // WorkflowRun partition key is taskId — query first to find the run (same pattern as PatchRunWorkspaceAsync).
        var q = new QueryDefinition("SELECT * FROM c WHERE c.id=@id").WithParameter("@id", runId);
        var iter = C("workflowRuns").GetItemQueryIterator<WorkflowRun>(q);
        WorkflowRun? run = null;
        while (iter.HasMoreResults && run is null)
            foreach (var r in await iter.ReadNextAsync(ct)) run = r;
        if (run is null) return;
        run.BranchName = branchName;
        run.PullRequestNumber = pullRequestNumber;
        await C("workflowRuns").ReplaceItemAsync(run, run.Id, new PartitionKey(run.TaskId), cancellationToken: ct);
    }

    public async Task<WorkflowRun?> GetRunByIdAsync(string runId, CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.id=@id").WithParameter("@id", runId);
        var iter = C("workflowRuns").GetItemQueryIterator<WorkflowRun>(q);
        while (iter.HasMoreResults)
            foreach (var r in await iter.ReadNextAsync(ct)) return r;
        return null;
    }

    // ── Artifact ──────────────────────────────────────────────────────────────

    public async Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default)
    {
        var res = await C("artifacts").CreateItemAsync(artifact, new PartitionKey(artifact.TaskId), cancellationToken: ct);
        _logger.LogDebug("[WorkflowCosmos] wrote Artifact id={Id} task={TaskId} version={Version}",
            res.Resource.Id, artifact.TaskId, artifact.Version);
        return res.Resource;
    }

    public async Task<List<Artifact>> GetUpstreamArtifactsAsync(IEnumerable<string> taskIds, CancellationToken ct = default)
    {
        var results = new List<Artifact>();
        foreach (var taskId in taskIds)
        {
            var query = new QueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.taskId = @taskId ORDER BY c.version DESC")
                .WithParameter("@taskId", taskId);

            var iter = C("artifacts").GetItemQueryIterator<Artifact>(query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(taskId) });

            while (iter.HasMoreResults)
                results.AddRange(await iter.ReadNextAsync(ct));
        }
        return results;
    }

    /// <summary>
    /// Returns the latest artifacts from validator tasks connected to taskId via QaFeedback edges.
    /// Used as additional context during QA loop retries so agents know what to fix.
    /// </summary>
    public async Task<List<Artifact>> GetQaFeedbackArtifactsAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var sourceIds = new List<string>();
        var q = new QueryDefinition(
            "SELECT VALUE c.sourceTaskId FROM c WHERE c.boardId=@b AND c.targetTaskId=@t AND c.kind='QaFeedback'")
            .WithParameter("@b", boardId).WithParameter("@t", taskId);
        var iter = C("taskEdges").GetItemQueryIterator<string>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
        while (iter.HasMoreResults) sourceIds.AddRange(await iter.ReadNextAsync(ct));

        if (sourceIds.Count == 0) return [];
        return await GetUpstreamArtifactsAsync(sourceIds, ct);
    }

    // ── Board ─────────────────────────────────────────────────────────────────

    public async Task<Board?> GetBoardAsync(string boardId, string tenantId, CancellationToken ct = default)
    {
        try
        {
            var res = await C("boards").ReadItemAsync<Board>(boardId, new PartitionKey(tenantId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<List<AgentTask>> GetBoardTasksAsync(string boardId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT c.id, c.title, c.description, c.status, c.assignee, c.taskBrief, c.artifactSummary FROM c WHERE c.boardId = @boardId")
            .WithParameter("@boardId", boardId);

        var iter = C("tasks").GetItemQueryIterator<AgentTask>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });

        var results = new List<AgentTask>();
        while (iter.HasMoreResults)
            results.AddRange(await iter.ReadNextAsync(ct));
        return results;
    }

    public async Task PatchTaskBriefAsync(string boardId, string taskId, string brief, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/taskBrief", brief) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    public async Task PatchTaskPendingOutputsAsync(string boardId, string taskId, List<Output> outputs, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/pendingOutputs", outputs) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    public async Task PatchTaskThreadIdAsync(string boardId, string taskId, string threadId, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/foundryThreadId", threadId) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    /// <summary>Persist the running count of request_human_input pauses for a task (repeat-ask guard, QA S1 §2.2).</summary>
    public async Task PatchTaskHumanAskCountAsync(string boardId, string taskId, int count, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/humanAskCount", count) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    /// <summary>Point the task at its newest artifact so the UI can cheaply find the latest deliverable (QA S2 §3.2).</summary>
    public async Task PatchTaskCurrentArtifactIdAsync(string boardId, string taskId, string artifactId, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/currentArtifactId", artifactId) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    /// <summary>/compact + /clear effect: set the brief (the summary, or "" for a plain clear), null the
    /// Foundry thread (fresh conversation next run), and set the ChatClearedAt transcript boundary.</summary>
    public async Task ResetTaskContextAsync(string boardId, string taskId, string brief, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation>
        {
            PatchOperation.Set("/taskBrief", brief),
            PatchOperation.Set<string?>("/foundryThreadId", null),
            PatchOperation.Set("/chatClearedAt", DateTimeOffset.UtcNow),
        };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    public async Task PatchArtifactSummaryAsync(string taskId, string artifactId, string summary, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/summary", summary) };
        await C("artifacts").PatchItemAsync<Artifact>(artifactId, new PartitionKey(taskId), patchOps, cancellationToken: ct);
    }

    public async Task PatchTaskArtifactSummaryAsync(string boardId, string taskId, string summary, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/artifactSummary", summary) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }

    // ── HumanInteraction ──────────────────────────────────────────────────────

    /// <summary>Idempotent create-or-replace by stable id — used for steerable interactions so a
    /// Durable activity retry can't create a duplicate request.</summary>
    public async Task<HumanInteraction> UpsertInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await C("humanInteractions").UpsertItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await C("humanInteractions").CreateItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        _logger.LogDebug("[WorkflowCosmos] wrote HumanInteraction id={Id} run={RunId}", res.Resource.Id, interaction.RunId);
        return res.Resource;
    }

    // ── RunEvent (steerable run trace) ────────────────────────────────────────

    public async Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default)
    {
        var res = await C("runEvents").CreateItemAsync(e, new PartitionKey(e.TaskId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<List<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default)
    {
        var sql = "SELECT * FROM c WHERE c.taskId = @taskId"
            + (sinceRound is null ? "" : " AND c.round >= @since")
            + " ORDER BY c.timestamp ASC";
        var q = new QueryDefinition(sql).WithParameter("@taskId", taskId);
        if (sinceRound is not null) q = q.WithParameter("@since", sinceRound.Value);

        var iter = C("runEvents").GetItemQueryIterator<RunEvent>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(taskId) });
        var results = new List<RunEvent>();
        while (iter.HasMoreResults) results.AddRange(await iter.ReadNextAsync(ct));
        return results;
    }

    // ── UsageEvents + UsageRollups ────────────────────────────────────────────

    /// <summary>Writes a usage event. Returns true if newly created, false if it already existed
    /// (409 Conflict) — callers skip rollup increments on false to avoid double counting.</summary>
    public async Task<bool> TryCreateUsageEventAsync(UsageEvent ev, CancellationToken ct = default)
    {
        try
        {
            await C(UsageEventsContainer).CreateItemAsync(ev, new PartitionKey(ev.TaskId), cancellationToken: ct);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;   // already recorded (write-level redelivery) — do not re-increment rollups
        }
    }

    /// <summary>Applies <paramref name="mutate"/> to the rollup with id <paramref name="id"/> under
    /// the tenant partition, race-safe via ETag optimistic concurrency with bounded retry. Creates the
    /// rollup (via <paramref name="create"/>) if it does not exist.</summary>
    public async Task UpdateRollupAsync(
        string tenantId, string id,
        Func<UsageRollup> create, Action<UsageRollup> mutate,
        CancellationToken ct = default)
    {
        var container = C(UsageRollupsContainer);
        var pk = new PartitionKey(tenantId);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            UsageRollup rollup;
            string? etag = null;
            try
            {
                var read = await container.ReadItemAsync<UsageRollup>(id, pk, cancellationToken: ct);
                rollup = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                rollup = create();
            }

            mutate(rollup);
            rollup.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                if (etag is null)
                    await container.CreateItemAsync(rollup, pk, cancellationToken: ct);
                else
                    await container.ReplaceItemAsync(rollup, id, pk,
                        new ItemRequestOptions { IfMatchEtag = etag }, ct);
                return;
            }
            catch (CosmosException e) when (
                e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed ||   // ETag mismatch — concurrent writer
                e.StatusCode == System.Net.HttpStatusCode.Conflict)               // create race — someone created it first
            {
                // re-read and retry
            }
        }
        throw new InvalidOperationException($"Rollup {id} update exhausted retries under contention.");
    }

    public async Task<UsageRollup?> GetRollupAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            var read = await C(UsageRollupsContainer).ReadItemAsync<UsageRollup>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return read.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    // ── TaskComments ──────────────────────────────────────────────────────────

    /// <summary>Returns all notes on a task that have been explicitly shared with the agent,
    /// ordered oldest-first. Only kind="note", sharedWithAgent=true, and not soft-deleted.</summary>
    public async Task<IReadOnlyList<TaskComment>> GetSharedTaskNotesAsync(string taskId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.taskId = @taskId AND c.kind = 'note' " +
            "AND c.sharedWithAgent = true AND (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt)) " +
            "ORDER BY c.createdAt ASC")
            .WithParameter("@taskId", taskId);

        var iter = C("taskComments").GetItemQueryIterator<TaskComment>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(taskId) });

        var results = new List<TaskComment>();
        while (iter.HasMoreResults)
            results.AddRange(await iter.ReadNextAsync(ct));
        return results;
    }

    // ── Board workspace (worktree architecture) ───────────────────────────────

    /// <summary>Reads a board and returns it together with its current ETag for CAS operations.</summary>
    public async Task<(Board board, string etag)> GetBoardWithEtagAsync(string boardId, string tenantId, CancellationToken ct = default)
    {
        var res = await C("boards").ReadItemAsync<Board>(boardId, new PartitionKey(tenantId), cancellationToken: ct);
        return (res.Resource, res.ETag);
    }

    /// <summary>Replaces a board using optimistic concurrency. Throws CosmosException(412) on ETag mismatch.</summary>
    public async Task ReplaceBoardAsync(Board board, string etag, CancellationToken ct = default)
    {
        await C("boards").ReplaceItemAsync(board, board.Id, new PartitionKey(board.TenantId),
            new ItemRequestOptions { IfMatchEtag = etag }, ct);
    }

    public async Task PatchBoardWorkspaceAsync(string boardId, string tenantId,
        string containerName, string endpoint, BoardWorkspaceStatus status, CancellationToken ct = default)
    {
        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/workspaceContainerName", containerName),
            PatchOperation.Set("/workspaceEndpoint",      endpoint),
            PatchOperation.Set("/workspaceStatus",        status.ToString()),
            PatchOperation.Set("/workspaceLastUsedAt",    DateTimeOffset.UtcNow),
        };
        await C("boards").PatchItemAsync<Board>(boardId, new PartitionKey(tenantId), ops, cancellationToken: ct);
    }

    public async Task PatchBoardWorkspaceStatusAsync(string boardId, string tenantId,
        BoardWorkspaceStatus status, CancellationToken ct = default)
    {
        var ops = new List<PatchOperation> { PatchOperation.Set("/workspaceStatus", status.ToString()) };
        await C("boards").PatchItemAsync<Board>(boardId, new PartitionKey(tenantId), ops, cancellationToken: ct);
    }

    public async Task PatchBoardWorkspaceLastUsedAsync(string boardId, string tenantId, CancellationToken ct = default)
    {
        var ops = new List<PatchOperation> { PatchOperation.Set("/workspaceLastUsedAt", DateTimeOffset.UtcNow) };
        await C("boards").PatchItemAsync<Board>(boardId, new PartitionKey(tenantId), ops, cancellationToken: ct);
    }

    /// <summary>Cross-partition query — returns all boards with workspaceStatus = 'Ready'.</summary>
    public async Task<List<Board>> GetBoardsWithActiveWorkspaceAsync(CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.workspaceStatus = 'Ready'");
        var iter = C("boards").GetItemQueryIterator<Board>(q);
        var results = new List<Board>();
        while (iter.HasMoreResults)
            results.AddRange(await iter.ReadNextAsync(ct));
        return results;
    }

    /// <summary>Returns true if any task on this board has status InProgress, AwaitingInteraction, or Blocked.</summary>
    public async Task<bool> HasActiveRunsForBoardAsync(string boardId, CancellationToken ct = default)
    {
        var q = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c WHERE c.boardId = @boardId AND c.status IN ('InProgress','AwaitingInteraction','Blocked')")
            .WithParameter("@boardId", boardId);
        var iter = C("tasks").GetItemQueryIterator<int>(q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
        while (iter.HasMoreResults)
            foreach (var count in await iter.ReadNextAsync(ct))
                return count > 0;
        return false;
    }

    private record TaskEdgeSlim(
        [property: JsonPropertyName("sourceTaskId")] string SourceTaskId,
        [property: JsonPropertyName("targetTaskId")] string TargetTaskId);
}
