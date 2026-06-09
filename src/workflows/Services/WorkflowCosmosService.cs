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

    // ── Board ─────────────────────────────────────────────────────────────────

    public async Task<Board?> GetBoardAsync(string boardId, CancellationToken ct = default)
    {
        try
        {
            var res = await C("boards").ReadItemAsync<Board>(boardId, new PartitionKey(boardId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<List<AgentTask>> GetBoardTasksAsync(string boardId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT c.id, c.title, c.status, c.taskBrief FROM c WHERE c.boardId = @boardId")
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

    // ── Approval ──────────────────────────────────────────────────────────────

    public async Task<Approval> CreateApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        var res = await C("approvals").CreateItemAsync(approval, new PartitionKey(approval.RunId), cancellationToken: ct);
        return res.Resource;
    }

    // ── HumanInteraction ──────────────────────────────────────────────────────

    public async Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await C("humanInteractions").CreateItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }

    // ── AuditLog ──────────────────────────────────────────────────────────────

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) =>
        await C("auditLog").CreateItemAsync(entry, new PartitionKey(entry.TenantId), cancellationToken: ct);
}
