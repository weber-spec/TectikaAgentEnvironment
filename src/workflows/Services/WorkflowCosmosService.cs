using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// Slim Cosmos DB access layer לשימוש מתוך Durable Function Activities.
/// </summary>
public class WorkflowCosmosService
{
    private readonly CosmosClient _client;
    private readonly string _db;

    public WorkflowCosmosService(CosmosClient client, IOptions<CosmosDbSettings> settings)
    {
        _client = client;
        _db = settings.Value.DatabaseName;
    }

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

    // ── AgentTask ─────────────────────────────────────────────────────────────

    public async Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        try
        {
            var res = await C("tasks").ReadItemAsync<AgentTask>(taskId, new PartitionKey(boardId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task UpdateTaskStatusAsync(string boardId, string taskId, AgentTaskStatus status, string? workflowRunId, CancellationToken ct = default)
    {
        var task = await GetTaskAsync(boardId, taskId, ct);
        if (task is null) return;
        task.Status = status;
        if (workflowRunId is not null) task.WorkflowRunId = workflowRunId;
        await C("tasks").ReplaceItemAsync(task, taskId, new PartitionKey(boardId), cancellationToken: ct);
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

    public async Task UpdateRunAsync(WorkflowRun run, CancellationToken ct = default) =>
        await C("workflowRuns").ReplaceItemAsync(run, run.Id, new PartitionKey(run.TaskId), cancellationToken: ct);

    // ── Artifact ──────────────────────────────────────────────────────────────

    public async Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default)
    {
        var res = await C("artifacts").CreateItemAsync(artifact, new PartitionKey(artifact.TaskId), cancellationToken: ct);
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

    // ── Approval ──────────────────────────────────────────────────────────────

    public async Task<Approval> CreateApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        var res = await C("approvals").CreateItemAsync(approval, new PartitionKey(approval.RunId), cancellationToken: ct);
        return res.Resource;
    }

    // ── AuditLog ──────────────────────────────────────────────────────────────

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) =>
        await C("auditLog").CreateItemAsync(entry, new PartitionKey(entry.TenantId), cancellationToken: ct);
}
