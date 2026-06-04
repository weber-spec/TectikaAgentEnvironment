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

    // Container names
    public const string BoardsContainer = "boards";
    public const string TasksContainer = "tasks";
    public const string AgentRolesContainer = "agentRoles";
    public const string WorkflowRunsContainer = "workflowRuns";
    public const string ArtifactsContainer = "artifacts";
    public const string ApprovalsContainer = "approvals";
    public const string AuditLogContainer = "auditLog";

    public CosmosDbService(CosmosClient client, IOptions<CosmosDbSettings> settings)
    {
        _client = client;
        _dbName = settings.Value.DatabaseName;
    }

    private Container GetContainer(string name) => _client.GetContainer(_dbName, name);

    // ── Bootstrap ────────────────────────────────────────────────────────────

    public async Task EnsureInfrastructureAsync()
    {
        var db = await _client.CreateDatabaseIfNotExistsAsync(_dbName);

        var containers = new (string Name, string PartitionKey)[]
        {
            (BoardsContainer,       "/tenantId"),
            (TasksContainer,        "/boardId"),
            (AgentRolesContainer,   "/tenantId"),
            (WorkflowRunsContainer, "/taskId"),
            (ArtifactsContainer,    "/taskId"),
            (ApprovalsContainer,    "/runId"),
            (AuditLogContainer,     "/tenantId"),
        };

        foreach (var (name, pk) in containers)
            await db.Database.CreateContainerIfNotExistsAsync(name, pk);
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
        return res.Resource;
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

    // ── Workflow Runs ─────────────────────────────────────────────────────────

    public async Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        var res = await GetContainer(WorkflowRunsContainer).CreateItemAsync(run, new PartitionKey(run.TaskId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(WorkflowRunsContainer).ReadItemAsync<WorkflowRun>(runId, new PartitionKey(taskId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        var res = await GetContainer(WorkflowRunsContainer).ReplaceItemAsync(run, run.Id, new PartitionKey(run.TaskId), cancellationToken: ct);
        return res.Resource;
    }

    // ── Artifacts ────────────────────────────────────────────────────────────

    public async Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default)
    {
        var res = await GetContainer(ArtifactsContainer).CreateItemAsync(artifact, new PartitionKey(artifact.TaskId), cancellationToken: ct);
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

    // ── Audit Log ─────────────────────────────────────────────────────────────

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) =>
        await GetContainer(AuditLogContainer).CreateItemAsync(entry, new PartitionKey(entry.TenantId), cancellationToken: ct);

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

        return results;
    }
}
