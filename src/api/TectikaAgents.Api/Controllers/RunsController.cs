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
    private readonly SseConnectionManager _sse;
    private readonly IRunStartService _runStart;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        ICosmosDbService cosmos,
        SseConnectionManager sse,
        IRunStartService runStart,
        ILogger<RunsController> logger)
    {
        _cosmos = cosmos;
        _sse = sse;
        _runStart = runStart;
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
        var savedRun = await _runStart.StartAsync(req.BoardId, req.TaskId, TenantId, ct);
        if (savedRun is null)
            return BadRequest("Task has no agent assigned or is already running.");

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
