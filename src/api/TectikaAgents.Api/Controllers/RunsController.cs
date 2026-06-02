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
    private readonly CosmosDbService _cosmos;
    private readonly SseConnectionManager _sse;

    public RunsController(CosmosDbService cosmos, SseConnectionManager sse)
    {
        _cosmos = cosmos;
        _sse = sse;
    }

    [HttpGet("{taskId}/{runId}")]
    public async Task<IActionResult> Get(string taskId, string runId, CancellationToken ct)
    {
        var run = await _cosmos.GetRunAsync(taskId, runId, ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>
    /// SSE endpoint — client מנוי לעדכונים חיים של run ספציפי.
    /// </summary>
    [HttpGet("{runId}/stream")]
    public async Task Stream(string runId, CancellationToken ct)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var client = new SseClient(new StreamWriter(Response.Body), ct);
        _sse.AddClient(runId, client);

        try
        {
            // Keep connection open until client disconnects
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _sse.RemoveClient(runId, client);
        }
    }
}
