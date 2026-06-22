using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Cosmos DB access layer — כל containers ופעולות CRUD בסיסיות.
/// </summary>
public class CosmosDbService : ICosmosDbService
{
    private readonly CosmosClient _client;
    private readonly string _dbName;
    private readonly ILogger<CosmosDbService> _logger;

    // Container names
    public const string BoardsContainer = "boards";
    public const string TasksContainer = "tasks";
    public const string AgentRolesContainer = "agentRoles";
    public const string WorkflowRunsContainer = "workflowRuns";
    public const string ArtifactsContainer = "artifacts";
    public const string HumanInteractionsContainer = "humanInteractions";
    public const string TaskEdgesContainer = "taskEdges";
    public const string RunEventsContainer = "runEvents";
    public const string PendingMessagesContainer = "pendingMessages";
    public const string NotificationsContainer = "notifications";
    public const string UserSettingsContainer = "userSettings";
    public const string UsageEventsContainer = "usageEvents";
    public const string UsageRollupsContainer = "usageRollups";
    public const string PreviewSessionsContainer = "previewSessions";

    /// <summary>Authoritative list of Cosmos containers this app requires (name + partition key).
    /// Source of truth for <see cref="EnsureInfrastructureAsync"/> and kept in sync with infra/modules/data.bicep.</summary>
    public static readonly (string Name, string PartitionKey)[] ContainerDefinitions =
    {
        (BoardsContainer,            "/tenantId"),
        (TasksContainer,             "/boardId"),
        (AgentRolesContainer,        "/tenantId"),
        (WorkflowRunsContainer,      "/taskId"),
        (ArtifactsContainer,         "/taskId"),
        (HumanInteractionsContainer, "/runId"),
        (TaskEdgesContainer,         "/boardId"),
        (RunEventsContainer,         "/taskId"),
        (PendingMessagesContainer,   "/runId"),
        (NotificationsContainer,     "/tenantId"),
        (UserSettingsContainer,      "/userId"),
        (UsageEventsContainer,       "/taskId"),
        (UsageRollupsContainer,      "/tenantId"),
        (PreviewSessionsContainer,   "/boardId"),
    };

    public CosmosDbService(CosmosClient client, IOptions<CosmosDbSettings> settings, ILogger<CosmosDbService> logger)
    {
        _client = client;
        _dbName = settings.Value.DatabaseName;
        _logger = logger;
    }

    private Container GetContainer(string name) => _client.GetContainer(_dbName, name);

    // ── Bootstrap ────────────────────────────────────────────────────────────

    public async Task EnsureInfrastructureAsync()
    {
        var db = await _client.CreateDatabaseIfNotExistsAsync(_dbName);

        foreach (var (name, pk) in ContainerDefinitions)
            await db.Database.CreateContainerIfNotExistsAsync(name, pk);

        _logger.LogInformation("[CosmosInfra] ensured database {Database} and {Count} containers", _dbName, ContainerDefinitions.Length);
    }

    // ── Boards ───────────────────────────────────────────────────────────────

    public async Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default)
    {
        var res = await GetContainer(BoardsContainer).CreateItemAsync(board, new PartitionKey(board.TenantId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId").WithParameter("@tenantId", tenantId);
        return await QueryAsync<Board>(BoardsContainer, query, tenantId, ct);
    }

    public async Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(BoardsContainer).ReadItemAsync<Board>(boardId, new PartitionKey(tenantId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default)
    {
        var res = await GetContainer(BoardsContainer).ReplaceItemAsync(board, board.Id, new PartitionKey(board.TenantId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default)
    {
        var tasks = await GetTasksByBoardAsync(boardId, ct);
        await System.Threading.Tasks.Task.WhenAll(tasks.Select(t =>
            GetContainer(TasksContainer).DeleteItemAsync<AgentTask>(t.Id, new PartitionKey(boardId), cancellationToken: ct)));

        try
        {
            await GetContainer(BoardsContainer).DeleteItemAsync<Board>(boardId, new PartitionKey(tenantId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone */ }

        // Best-effort: remove the board's own usage rollup (never touches project lifetime totals).
        try
        {
            await GetContainer(UsageRollupsContainer).DeleteItemAsync<UsageRollup>(
                UsageRollup.BoardId(boardId), new PartitionKey(tenantId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        catch (Exception ex) { _logger.LogWarning(ex, "[Usage] board rollup cleanup failed {BoardId}", boardId); }
    }

    // ── Tasks ────────────────────────────────────────────────────────────────

    public async Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default)
    {
        var res = await GetContainer(TasksContainer).CreateItemAsync(task, new PartitionKey(task.BoardId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(TasksContainer).ReadItemAsync<AgentTask>(taskId, new PartitionKey(boardId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.boardId = @boardId").WithParameter("@boardId", boardId);
        return await QueryAsync<AgentTask>(TasksContainer, query, boardId, ct);
    }

    public async Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default)
    {
        var res = await GetContainer(TasksContainer).ReplaceItemAsync(task, task.Id, new PartitionKey(task.BoardId), cancellationToken: ct);
        _logger.LogDebug("[CosmosWrite] update AgentTask id={Id} status={Status}", task.Id, task.Status);
        return res.Resource;
    }

    public async Task<AgentTask?> TryClaimTaskForRunAsync(string boardId, string taskId, string runId, string sessionId, CancellationToken ct = default)
    {
        var container = GetContainer(TasksContainer);
        var pk = new PartitionKey(boardId);

        AgentTask task;
        string? etag;
        try
        {
            var read = await container.ReadItemAsync<AgentTask>(taskId, pk, cancellationToken: ct);
            task = read.Resource;
            etag = read.ETag;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }

        if (task.Status != AgentTaskStatus.Backlog) return null;   // already running / done / etc.

        task.Status         = AgentTaskStatus.InProgress;
        task.WorkflowRunId  = runId;
        task.UsageSessionId ??= sessionId;
        try
        {
            // IfMatchEtag makes this a compare-and-set: if any other writer touched the task since our
            // read (e.g. a concurrent claim), the replace fails with 412 and we report the lost race
            // instead of clobbering it / double-running.
            var res = await container.ReplaceItemAsync(task, taskId, pk,
                new ItemRequestOptions { IfMatchEtag = etag }, ct);
            _logger.LogInformation("[CosmosWrite] claimed task {Id} for run {RunId}", taskId, runId);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            _logger.LogInformation("[CosmosWrite] task {Id} claim lost the race (etag mismatch)", taskId);
            return null;
        }
    }

    public async Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        // Capture tenantId before deleting so we can clean up usage rollup.
        string? tenantId = null;
        try
        {
            var task = await GetContainer(TasksContainer).ReadItemAsync<AgentTask>(taskId, new PartitionKey(boardId), cancellationToken: ct);
            tenantId = task.Resource.TenantId;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone — nothing to clean up */ return; }

        try
        {
            await GetContainer(TasksContainer).DeleteItemAsync<AgentTask>(taskId, new PartitionKey(boardId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone */ }

        // Best-effort: remove the task's own usage rollup (never touches project/board lifetime totals).
        try
        {
            await GetContainer(UsageRollupsContainer).DeleteItemAsync<UsageRollup>(
                UsageRollup.TaskId(taskId), new PartitionKey(tenantId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        catch (Exception ex) { _logger.LogWarning(ex, "[Usage] task rollup cleanup failed {TaskId}", taskId); }

        // Best-effort: remove all usage events for this task (partitioned by /taskId).
        try
        {
            var eventIds = await QueryAsync<string>(
                UsageEventsContainer,
                new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.taskId = @t").WithParameter("@t", taskId),
                taskId, ct);
            foreach (var id in eventIds)
                await GetContainer(UsageEventsContainer).DeleteItemAsync<UsageEvent>(id, new PartitionKey(taskId), cancellationToken: ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Usage] task events cleanup failed {TaskId}", taskId); }
    }

    // ── Agent Roles ───────────────────────────────────────────────────────────

    public async Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId").WithParameter("@tenantId", tenantId);
        return await QueryAsync<AgentRole>(AgentRolesContainer, query, tenantId, ct);
    }

    public async Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default)
    {
        var res = await GetContainer(AgentRolesContainer).UpsertItemAsync(role, new PartitionKey(role.TenantId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(AgentRolesContainer).ReadItemAsync<AgentRole>(roleId, new PartitionKey(tenantId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
    {
        try
        {
            await GetContainer(AgentRolesContainer).DeleteItemAsync<AgentRole>(roleId, new PartitionKey(tenantId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone */ }
    }

    // ── Workflow Runs ─────────────────────────────────────────────────────────

    public async Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        var res = await GetContainer(WorkflowRunsContainer).CreateItemAsync(run, new PartitionKey(run.TaskId), cancellationToken: ct);
        _logger.LogDebug("[CosmosWrite] create WorkflowRun id={Id} task={TaskId}", res.Resource.Id, run.TaskId);
        return res.Resource;
    }

    public async Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(WorkflowRunsContainer).ReadItemAsync<WorkflowRun>(runId, new PartitionKey(taskId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("[CosmosRead] WorkflowRun not found id={Id} task={TaskId}", runId, taskId);
            return null;
        }
    }

    public async Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string taskId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.taskId = @taskId").WithParameter("@taskId", taskId);
        return await QueryAsync<WorkflowRun>(WorkflowRunsContainer, query, taskId, ct);
    }

    public async Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        var res = await GetContainer(WorkflowRunsContainer).ReplaceItemAsync(run, run.Id, new PartitionKey(run.TaskId), cancellationToken: ct);
        _logger.LogDebug("[CosmosWrite] update WorkflowRun id={Id} status={Status}", run.Id, run.Status);
        return res.Resource;
    }

    // ── Artifacts ────────────────────────────────────────────────────────────

    public async Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default)
    {
        var res = await GetContainer(ArtifactsContainer).CreateItemAsync(artifact, new PartitionKey(artifact.TaskId), cancellationToken: ct);
        _logger.LogDebug("[CosmosWrite] create Artifact id={Id} task={TaskId}", res.Resource.Id, artifact.TaskId);
        return res.Resource;
    }

    public async Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.taskId = @taskId ORDER BY c.version DESC").WithParameter("@taskId", taskId);
        return await QueryAsync<Artifact>(ArtifactsContainer, query, taskId, ct);
    }


    // ── Human Interactions ─────────────────────────────────────────────────────

    public async Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await GetContainer(HumanInteractionsContainer).CreateItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(HumanInteractionsContainer).ReadItemAsync<HumanInteraction>(interactionId, new PartitionKey(runId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await GetContainer(HumanInteractionsContainer).ReplaceItemAsync(interaction, interaction.Id, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = 'Pending'")
            .WithParameter("@tenantId", tenantId);
        return await QueryAsync<HumanInteraction>(HumanInteractionsContainer, query, null, ct);
    }

    // ── Edges ─────────────────────────────────────────────────────────────────

    public async Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
    {
        var res = await GetContainer(TaskEdgesContainer)
            .CreateItemAsync(edge, new PartitionKey(edge.BoardId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.boardId = @boardId").WithParameter("@boardId", boardId);
        return await QueryAsync<TaskEdge>(TaskEdgesContainer, query, boardId, ct);
    }

    public async Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(TaskEdgesContainer)
                .ReadItemAsync<TaskEdge>(edgeId, new PartitionKey(boardId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
    {
        edge.UpdatedAt = DateTimeOffset.UtcNow;
        var res = await GetContainer(TaskEdgesContainer)
            .ReplaceItemAsync(edge, edge.Id, new PartitionKey(edge.BoardId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
    {
        try { await GetContainer(TaskEdgesContainer).DeleteItemAsync<TaskEdge>(edgeId, new PartitionKey(boardId), cancellationToken: ct); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    public async Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.boardId = @boardId AND (c.sourceTaskId = @taskId OR c.targetTaskId = @taskId)")
            .WithParameter("@boardId", boardId).WithParameter("@taskId", taskId);
        foreach (var e in await QueryAsync<TaskEdge>(TaskEdgesContainer, query, boardId, ct))
            await DeleteEdgeAsync(boardId, e.Id, ct);
    }

    // ── Run trace ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default)
    {
        var sql = "SELECT * FROM c WHERE c.taskId = @taskId"
            + (sinceRound is null ? "" : " AND c.round >= @since")
            + " ORDER BY c.timestamp ASC";
        var query = new QueryDefinition(sql).WithParameter("@taskId", taskId);
        if (sinceRound is not null) query = query.WithParameter("@since", sinceRound.Value);
        return (await QueryAsync<RunEvent>(RunEventsContainer, query, taskId, ct)).ToList();
    }

    public async Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default)
    {
        var res = await GetContainer(RunEventsContainer).CreateItemAsync(e, new PartitionKey(e.TaskId), cancellationToken: ct);
        return res.Resource;
    }

    // ── Usage ─────────────────────────────────────────────────────────────────

    public async Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default)
    {
        var id = UsageRollup.TaskId(taskId);
        var container = GetContainer(UsageRollupsContainer);
        var pk = new PartitionKey(tenantId);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            UsageRollup rollup; string? etag = null;
            try { var read = await container.ReadItemAsync<UsageRollup>(id, pk, cancellationToken: ct); rollup = read.Resource; etag = read.ETag; }
            catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return; } // nothing to reset yet
            rollup.CurrentSession = new SessionBucket { SessionId = newSessionId, Since = DateTimeOffset.UtcNow };
            rollup.UpdatedAt = DateTimeOffset.UtcNow;
            try { await container.ReplaceItemAsync(rollup, id, pk, new ItemRequestOptions { IfMatchEtag = etag }, ct); return; }
            catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed) { /* retry */ }
        }
        _logger.LogWarning("[Usage] ResetTaskUsageSession exhausted retries for task {TaskId} (tenant {TenantId})", taskId, tenantId);
    }

    public async Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            var r = await GetContainer(UsageRollupsContainer).ReadItemAsync<UsageRollup>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return r.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @t").WithParameter("@t", tenantId);
        return (await QueryAsync<UsageRollup>(UsageRollupsContainer, q, tenantId, ct)).ToList();
    }

    public async Task UpsertUsageRollupAsync(UsageRollup rollup, CancellationToken ct = default)
        => await GetContainer(UsageRollupsContainer).UpsertItemAsync(rollup, new PartitionKey(rollup.TenantId), cancellationToken: ct);

    public async Task UpsertUsageEventAsync(UsageEvent ev, CancellationToken ct = default)
        => await GetContainer(UsageEventsContainer).UpsertItemAsync(ev, new PartitionKey(ev.TaskId), cancellationToken: ct);

    public async Task<UsageEventsPage> GetUsageEventsForTaskAsync(string tenantId, string taskId, int max, string? continuationToken, CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.taskId = @t AND c.tenantId = @tenant ORDER BY c.timestamp DESC")
            .WithParameter("@t", taskId).WithParameter("@tenant", tenantId);
        var it = GetContainer(UsageEventsContainer).GetItemQueryIterator<UsageEvent>(q, continuationToken,
            new QueryRequestOptions { PartitionKey = new PartitionKey(taskId), MaxItemCount = max });
        var page = new UsageEventsPage();
        if (it.HasMoreResults)
        {
            var resp = await it.ReadNextAsync(ct);
            page.Items.AddRange(resp);
            page.ContinuationToken = resp.ContinuationToken;
        }
        return page;
    }

    public async Task<List<UsageTimePoint>> GetUsageTimeSeriesAsync(string scope, string scopeId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var today = DateTimeOffset.UtcNow.Date;
        var since = today.AddDays(-(days - 1));
        var field = scope == "board" ? "boardId" : "tenantId";   // events carry both
        var q = new QueryDefinition($"SELECT * FROM c WHERE c.{field} = @id AND c.timestamp >= @since")
            .WithParameter("@id", scopeId)
            .WithParameter("@since", new DateTimeOffset(since, TimeSpan.Zero));
        // Cross-partition (events are partitioned by /taskId); bounded by the time window.
        var events = await QueryAsync<UsageEvent>(UsageEventsContainer, q, partitionKey: null, ct);

        var byDay = new Dictionary<string, UsageTimePoint>();
        for (var i = 0; i < days; i++)
        {
            var key = since.AddDays(i).ToString("yyyy-MM-dd");
            byDay[key] = new UsageTimePoint { Date = key };
        }
        foreach (var e in events)
        {
            var key = e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
            if (!byDay.TryGetValue(key, out var pt)) continue;   // outside window (defensive)
            pt.Tokens += e.Usage.Total;
            pt.CostUsd += e.CostUsd;
        }
        return byDay.Values.OrderBy(p => p.Date).ToList();
    }

    // ── Preview Sessions ──────────────────────────────────────────────────────

    public async Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default)
    {
        var q = new QueryDefinition(
            "SELECT * FROM c WHERE c.boardId = @b AND c.status IN ('Provisioning','Running') ORDER BY c.createdAt DESC")
            .WithParameter("@b", boardId);
        using var it = GetContainer(PreviewSessionsContainer).GetItemQueryIterator<PreviewSession>(
            q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId), MaxItemCount = 1 });
        if (it.HasMoreResults)
            foreach (var s in await it.ReadNextAsync(ct)) return s;
        return null;
    }

    public async Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default) =>
        await GetContainer(PreviewSessionsContainer).UpsertItemAsync(session, new PartitionKey(session.BoardId), cancellationToken: ct);

    public async Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default)
    {
        try { await GetContainer(PreviewSessionsContainer).DeleteItemAsync<PreviewSession>(id, new PartitionKey(boardId), cancellationToken: ct); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    public async Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.status IN ('Provisioning','Running')");
        using var it = GetContainer(PreviewSessionsContainer).GetItemQueryIterator<PreviewSession>(q);
        var list = new List<PreviewSession>();
        while (it.HasMoreResults) list.AddRange(await it.ReadNextAsync(ct));
        return list;
    }

    // ── Generic query helper ──────────────────────────────────────────────────

    private async Task<IEnumerable<T>> QueryAsync<T>(string containerName, QueryDefinition query, string? partitionKey, CancellationToken ct)
    {
        var options = partitionKey is not null
            ? new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) }
            : null;

        var iterator = GetContainer(containerName).GetItemQueryIterator<T>(query, requestOptions: options);
        var results = new List<T>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }

        _logger.LogDebug("[CosmosRead] query {Type} container={Container} -> {Count} items", typeof(T).Name, containerName, results.Count);

        return results;
    }
}
