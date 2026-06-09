using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/runs")]
[Authorize]
public class RunsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly SseConnectionManager _sse;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        ICosmosDbService cosmos,
        SseConnectionManager sse,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<RunsController> logger)
    {
        _cosmos = cosmos;
        _sse = sse;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet("{taskId}/{runId}")]
    public async Task<IActionResult> Get(string taskId, string runId, CancellationToken ct)
    {
        var run = await _cosmos.GetRunAsync(taskId, runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>
    /// POST /api/runs/start — יוצר WorkflowRun ב-Cosmos ומפעיל Durable Functions pipeline.
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRunRequest req, CancellationToken ct)
    {
        // ── 1. Load task ──────────────────────────────────────────────────────
        var task = await _cosmos.GetTaskAsync(req.BoardId, req.TaskId, ct);
        if (task is null) return NotFound($"Task '{req.TaskId}' not found.");

        // ── 2. Resolve pipeline (supplied or derived from task assignee) ─────────
        List<PipelineStep> pipeline;
        try
        {
            pipeline = (req.Pipeline is { Count: > 0 }) ? req.Pipeline : RunPipelineFactory.FromTask(task);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        // ── 3. Create WorkflowRun in Cosmos ───────────────────────────────────
        var run = new WorkflowRun
        {
            TenantId           = TenantId,
            TaskId             = req.TaskId,
            PipelineDefinition = pipeline,
            Status             = RunStatus.Pending
        };

        var savedRun = await _cosmos.CreateRunAsync(run, ct);

        // Update task's workflowRunId
        task.WorkflowRunId = savedRun.Id;
        task.Status = AgentTaskStatus.InProgress;
        await _cosmos.UpdateTaskAsync(task, ct);

        // ── 4. Trigger Durable Functions via HTTP ─────────────────────────────
        var durableStartUrl = _config["DurableFunctions:StartUrl"]
            ?? "http://localhost:7071/api/pipelines/start";

        var pipelineInput = new
        {
            runId    = savedRun.Id,
            taskId   = req.TaskId,
            boardId  = req.BoardId,
            tenantId = TenantId,
            steps    = pipeline
        };

        try
        {
            var http = _httpFactory.CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(pipelineInput), Encoding.UTF8, "application/json");
            var durableRes = await http.PostAsync(durableStartUrl, content, ct);

            if (!durableRes.IsSuccessStatusCode)
            {
                var err = await durableRes.Content.ReadAsStringAsync(ct);
                _logger.LogError("Durable Functions start failed: {Status} {Error}", durableRes.StatusCode, err);
                return StatusCode(502, $"Failed to start pipeline: {err}");
            }

            var durableBody = await durableRes.Content.ReadAsStringAsync(ct);
            var durableData = JsonSerializer.Deserialize<JsonElement>(durableBody);
            var instanceId = durableData.TryGetProperty("instanceId", out var id) ? id.GetString() : null;

            // Save instance ID back to run
            savedRun.DurableFunctionInstanceId = instanceId;
            await _cosmos.UpdateRunAsync(savedRun, ct);

            _logger.LogInformation("Pipeline started: run={RunId} instance={InstanceId}", savedRun.Id, instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger Durable Functions for run {RunId}", savedRun.Id);
            // Don't fail the request — run is created, can retry trigger
        }

        return CreatedAtAction(nameof(Get), new { taskId = req.TaskId, runId = savedRun.Id }, new
        {
            runId      = savedRun.Id,
            taskId     = req.TaskId,
            status     = savedRun.Status,
            streamUrl  = $"/api/runs/{savedRun.Id}/stream"
        });
    }

    /// <summary>
    /// SSE endpoint — client מנוי לעדכונים חיים של run ספציפי.
    /// </summary>
    [HttpGet("{runId}/stream")]
    [AllowAnonymous] // SSE streams — auth via query token in Phase 2
    public async Task Stream(string runId, CancellationToken ct)
    {
        Response.Headers["Content-Type"]      = "text/event-stream";
        Response.Headers["Cache-Control"]     = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var client = new SseClient(new StreamWriter(Response.Body), ct);
        _sse.AddClient(runId, client);

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        finally { _sse.RemoveClient(runId, client); }
    }
}

public record StartRunRequest(
    string TaskId,
    string BoardId,
    List<PipelineStep>? Pipeline);
