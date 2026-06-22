using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;

namespace TectikaAgents.Api.Controllers;

public sealed record StartPreviewRequest(string Branch);

[ApiController]
[Route("api/boards/{boardId}/preview")]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly IPreviewService _preview;
    public PreviewController(IPreviewService preview) => _preview = preview;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    [HttpPost]
    public async Task<IActionResult> Start(string boardId, [FromBody] StartPreviewRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Branch)) return BadRequest(new { error = "branch is required" });
        try { return Ok(await _preview.StartAsync(TenantId, boardId, req.Branch, ct)); }
        catch (PreviewNotConnectedException) { return Conflict(new { error = "GitHubNotConnected" }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet]
    public async Task<IActionResult> Get(string boardId, CancellationToken ct)
    {
        var s = await _preview.GetAsync(TenantId, boardId, ct);
        return s is null ? NotFound() : Ok(s);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(string boardId, CancellationToken ct)
    {
        var s = await _preview.HeartbeatAsync(TenantId, boardId, ct);
        return s is null ? NotFound() : Ok(s);
    }

    [HttpDelete]
    public async Task<IActionResult> Stop(string boardId, CancellationToken ct)
    {
        await _preview.StopAsync(TenantId, boardId, ct);
        return NoContent();
    }
}
