using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/runs")]
[Authorize]
public class RunsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly SseHub _hub;
    private readonly IRunStartService _runStart;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        ICosmosDbService cosmos,
        SseHub hub,
        IRunStartService runStart,
        ILogger<RunsController> logger)
    {
        _cosmos = cosmos;
        _hub = hub;
        _runStart = runStart;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet("{taskId}/{runId}")]
    public async Task<IActionResult> Get(string taskId, string runId, CancellationToken ct)
    {
        _logger.LogInformation("[RunGet] fetching run {RunId} for task {TaskId}", runId, taskId);
        var run = await _cosmos.GetRunAsync(taskId, runId, ct);
        if (run is null)
        {
            _logger.LogWarning("[RunGet] run {RunId} not found for task {TaskId}", runId, taskId);
            return NotFound();
        }

        _logger.LogInformation("[RunGet] returning run {RunId} status={Status}", run.Id, run.Status);
        return Ok(run);
    }

    /// <summary>
    /// POST /api/runs/start — יוצר WorkflowRun ב-Cosmos ומפעיל Durable Functions pipeline.
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRunRequest req, CancellationToken ct)
    {
        _logger.LogInformation("[RunStart] received start request board={BoardId} task={TaskId}", req.BoardId, req.TaskId);
        var savedRun = await _runStart.StartAsync(req.BoardId, req.TaskId, TenantId, ct);
        if (savedRun is null)
        {
            _logger.LogWarning("[RunStart] task {TaskId} on board {BoardId} has no agent assigned or is already running", req.TaskId, req.BoardId);
            return BadRequest("Task has no agent assigned or is already running.");
        }

        _logger.LogInformation("[RunStart] run {RunId} started for task {TaskId} status={Status}", savedRun.Id, req.TaskId, savedRun.Status);
        return CreatedAtAction(nameof(Get), new { taskId = req.TaskId, runId = savedRun.Id }, new
        {
            runId     = savedRun.Id,
            taskId    = req.TaskId,
            status    = savedRun.Status,
            streamUrl = $"/api/runs/{savedRun.Id}/stream"
        });
    }

    /// <summary>
    /// SSE endpoint — client מנוי לעדכונים חיים של run ספציפי.
    /// </summary>
    [HttpGet("{runId}/stream")]
    [AllowAnonymous] // SSE streams — auth via query token in Phase 2
    public Task Stream(string runId, CancellationToken ct) =>
        SseEndpoint.RunAsync(Response, _hub, SseKeys.Run(runId), ct, _logger);
}

public record StartRunRequest(
    string TaskId,
    string BoardId);
