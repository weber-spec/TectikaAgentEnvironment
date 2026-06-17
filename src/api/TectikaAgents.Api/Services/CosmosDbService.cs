using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

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
    public const string ApprovalsContainer = "approvals";
    public const string AuditLogContainer = "auditLog";
    public const string HumanInteractionsContainer = "humanInteractions";
    public const string TaskEdgesContainer = "taskEdges";
    public const string RunEventsContainer = "runEvents";
    public const string PendingMessagesContainer = "pendingMessages";
    public const string NotificationsContainer = "notifications";
    public const string UserSettingsContainer = "userSettings";
    public const string UsageEventsContainer = "usageEvents";
    public const string UsageRollupsContainer = "usageRollups";

    /// <summary>Authoritative list of Cosmos containers this app requires (name + partition key).
    /// Source of truth for <see cref="EnsureInfrastructureAsync"/> and kept in sync with infra/modules/data.bicep.</summary>
    public static readonly (string Name, string PartitionKey)[] ContainerDefinitions =
    {
        (BoardsContainer,            "/tenantId"),
        (TasksContainer,             "/boardId"),
        (AgentRolesContainer,        "/tenantId"),
        (WorkflowRunsContainer,      "/taskId"),
        (ArtifactsContainer,         "/taskId"),
        (ApprovalsContainer,         "/runId"),
        (AuditLogContainer,          "/tenantId"),
        (HumanInteractionsContainer, "/runId"),
        (TaskEdgesContainer,         "/boardId"),
        (RunEventsContainer,         "/taskId"),
        (PendingMessagesContainer,   "/runId"),
        (NotificationsContainer,     "/tenantId"),
        (UserSettingsContainer,      "/userId"),
        (UsageEventsContainer,       "/taskId"),
        (UsageRollupsContainer,      "/tenantId"),
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

    public async Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        try
        {
            await GetContainer(TasksContainer).DeleteItemAsync<AgentTask>(taskId, new PartitionKey(boardId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone */ }
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

    // ── Approvals ─────────────────────────────────────────────────────────────

    public async Task<Approval> CreateApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        var res = await GetContainer(ApprovalsContainer).CreateItemAsync(approval, new PartitionKey(approval.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<Approval?> GetApprovalAsync(string runId, string approvalId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(ApprovalsContainer).ReadItemAsync<Approval>(approvalId, new PartitionKey(runId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<Approval> UpdateApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        var res = await GetContainer(ApprovalsContainer).ReplaceItemAsync(approval, approval.Id, new PartitionKey(approval.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<IEnumerable<Approval>> GetPendingApprovalsAsync(string tenantId, CancellationToken ct = default)
    {
        // Cross-partition query — acceptable for approval inbox (infrequent)
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = 'Pending'").WithParameter("@tenantId", tenantId);
        return await QueryAsync<Approval>(ApprovalsContainer, query, null, ct);
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

    // ── Audit Log ─────────────────────────────────────────────────────────────

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) =>
        await GetContainer(AuditLogContainer).CreateItemAsync(entry, new PartitionKey(entry.TenantId), cancellationToken: ct);

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
