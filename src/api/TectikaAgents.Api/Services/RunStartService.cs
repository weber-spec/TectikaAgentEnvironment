using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Core.Configuration;
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
    private readonly DurableFunctionsSettings _settings;
    private readonly ILogger<RunStartService> _logger;

    public RunStartService(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IOptions<DurableFunctionsSettings> settings,
        ILogger<RunStartService> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<WorkflowRun?> StartAsync(string boardId, string taskId, string tenantId, CancellationToken ct = default)
    {
        _logger.LogInformation("[RunStart] StartAsync board={BoardId} task={TaskId} tenant={TenantId}", boardId, taskId, tenantId);

        // ── 1. Load task ──────────────────────────────────────────────────────
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("[RunStart] could not start run for task {TaskId} (not found on board {BoardId})", taskId, boardId);
            return null;
        }

        // ── 2. Guard: only start Backlog tasks ────────────────────────────────
        if (task.Status != AgentTaskStatus.Backlog)
        {
            _logger.LogWarning("[RunStart] could not start run for task {TaskId} (not eligible, status {Status})", taskId, task.Status);
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
            _logger.LogWarning(ex, "[RunStart] could not start run for task {TaskId} (cannot build pipeline: {Message})", taskId, ex.Message);
            return null;
        }

        // ── 4. Create WorkflowRun in Cosmos ───────────────────────────────────
        var run = new WorkflowRun
        {
            TenantId           = tenantId,
            TaskId             = taskId,
            PipelineDefinition = pipeline,
            Status             = RunStatus.Pending,
            PreviousTaskStatus = task.Status
        };

        var savedRun = await _cosmos.CreateRunAsync(run, ct);
        _logger.LogInformation("[RunStart] created run {RunId} for task {TaskId}", savedRun.Id, taskId);

        // ── 5. Update task: link run + mark InProgress ────────────────────────
        task.WorkflowRunId = savedRun.Id;
        task.Status        = AgentTaskStatus.InProgress;
        await _cosmos.UpdateTaskAsync(task, ct);

        // ── 6. Trigger Durable Functions via HTTP ─────────────────────────────
        var durableStartUrl = _settings.StartUrl;
        if (!string.IsNullOrEmpty(_settings.FunctionKey))
            durableStartUrl += $"?code={Uri.EscapeDataString(_settings.FunctionKey)}";

        var pipelineInput = new
        {
            RunId    = savedRun.Id,
            TaskId   = taskId,
            BoardId  = boardId,
            TenantId = tenantId,
            Steps    = pipeline
        };

        try
        {
            var http    = _httpFactory.CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(pipelineInput), Encoding.UTF8, "application/json");
            var res     = await http.PostAsync(durableStartUrl, content, ct);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.LogError("[RunStart] Durable Functions start failed for run {RunId} status={Status} error={Error}", savedRun.Id, res.StatusCode, err);
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
                    _logger.LogInformation("[RunStart] pipeline started run={RunId} instance={InstanceId}", savedRun.Id, instanceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RunStart] failed to trigger Durable Functions for run {RunId}", savedRun.Id);
            // Don't throw — run is created, trigger can be retried
        }

        return savedRun;
    }
}
