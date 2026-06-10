using System.Text;
using System.Text.Json;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public interface IRunStartService
{
    /// <summary>
    /// Creates a WorkflowRun for the given task, updates the task status to InProgress,
    /// and triggers the Durable Functions pipeline. Returns null if the task is not
    /// eligible (not found, not Backlog, or has no assigned agent).
    /// </summary>
    Task<WorkflowRun?> StartAsync(string boardId, string taskId, string tenantId, CancellationToken ct = default);
}

public class RunStartService : IRunStartService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RunStartService> _logger;

    public RunStartService(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<RunStartService> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<WorkflowRun?> StartAsync(string boardId, string taskId, string tenantId, CancellationToken ct = default)
    {
        // ── 1. Load task ──────────────────────────────────────────────────────
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("Task {TaskId} not found on board {BoardId}", taskId, boardId);
            return null;
        }

        // ── 2. Guard: only start Backlog tasks ────────────────────────────────
        if (task.Status != AgentTaskStatus.Backlog)
        {
            _logger.LogInformation("Task {TaskId} is {Status}, skipping", taskId, task.Status);
            return null;
        }

        // ── 3. Resolve pipeline from task assignee ────────────────────────────
        List<PipelineStep> pipeline;
        try
        {
            pipeline = RunPipelineFactory.FromTask(task);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot build pipeline for task {TaskId}: {Message}", taskId, ex.Message);
            return null;
        }

        // ── 4. Create WorkflowRun in Cosmos ───────────────────────────────────
        var run = new WorkflowRun
        {
            TenantId           = tenantId,
            TaskId             = taskId,
            PipelineDefinition = pipeline,
            Status             = RunStatus.Pending
        };

        var savedRun = await _cosmos.CreateRunAsync(run, ct);

        // ── 5. Update task: link run + mark InProgress ────────────────────────
        task.WorkflowRunId = savedRun.Id;
        task.Status        = AgentTaskStatus.InProgress;
        await _cosmos.UpdateTaskAsync(task, ct);

        // ── 6. Trigger Durable Functions via HTTP ─────────────────────────────
        var durableStartUrl = _config["DurableFunctions:StartUrl"]
            ?? "http://localhost:7071/api/pipelines/start";

        var pipelineInput = new
        {
            runId    = savedRun.Id,
            taskId,
            boardId,
            tenantId,
            steps    = pipeline
        };

        try
        {
            var http    = _httpFactory.CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(pipelineInput), Encoding.UTF8, "application/json");
            var res     = await http.PostAsync(durableStartUrl, content, ct);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.LogError("Durable Functions start failed for run {RunId}: {Status} {Error}", savedRun.Id, res.StatusCode, err);
            }
            else
            {
                var body       = await res.Content.ReadAsStringAsync(ct);
                var durableData = JsonSerializer.Deserialize<JsonElement>(body);
                var instanceId  = durableData.TryGetProperty("instanceId", out var id) ? id.GetString() : null;

                // ── 7. Persist instanceId back to run ─────────────────────────
                if (instanceId is not null)
                {
                    savedRun.DurableFunctionInstanceId = instanceId;
                    await _cosmos.UpdateRunAsync(savedRun, ct);
                    _logger.LogInformation("Pipeline started: run={RunId} instance={InstanceId}", savedRun.Id, instanceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger Durable Functions for run {RunId}", savedRun.Id);
            // Don't throw — run is created, trigger can be retried
        }

        return savedRun;
    }
}
