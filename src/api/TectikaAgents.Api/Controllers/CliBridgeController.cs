using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/tasks/{taskId}/cli")]
[Authorize]
public class CliBridgeController : ControllerBase
{
    private readonly CliBridgeManager _bridge;
    private readonly ICosmosDbService _cosmos;

    public CliBridgeController(CliBridgeManager bridge, ICosmosDbService cosmos)
    {
        _bridge = bridge;
        _cosmos = cosmos;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    /// <summary>
    /// WebSocket endpoint לחיבור CLI agent חיצוני.
    /// Usage: agentboard link --task-id {taskId} --run-id {runId}
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(string taskId, [FromQuery] string runId, CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await _bridge.HandleConnectionAsync(taskId, runId, TenantId, ws, ct);
    }

    [HttpGet("status")]
    public IActionResult Status(string taskId) =>
        Ok(new { taskId, connected = _bridge.IsConnected(taskId) });
}
