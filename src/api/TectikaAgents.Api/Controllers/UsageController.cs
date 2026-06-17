using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/usage")]
[Authorize]
public class UsageController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly CostCalculator _cost;
    private readonly ILogger<UsageController> _logger;

    public UsageController(ICosmosDbService cosmos, CostCalculator cost, ILogger<UsageController> logger)
    {
        _cosmos = cosmos;
        _cost = cost;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    // ── Empty rollup helpers ─────────────────────────────────────────────────

    private static UsageRollup EmptyProject(string t) =>
        new() { Id = UsageRollup.ProjectId(t), TenantId = t, Scope = UsageScope.Project, ScopeId = t };

    private static UsageRollup EmptyBoard(string t, string b) =>
        new() { Id = UsageRollup.BoardId(b), TenantId = t, Scope = UsageScope.Board, ScopeId = b };

    private static UsageRollup EmptyTask(string t, string k) =>
        new() { Id = UsageRollup.TaskId(k), TenantId = t, Scope = UsageScope.Task, ScopeId = k };

    // ── Routes ───────────────────────────────────────────────────────────────

    /// <summary>GET /api/usage/project — project-level rollup for the caller's tenant.</summary>
    [HttpGet("project")]
    public async Task<IActionResult> GetProject(CancellationToken ct)
    {
        var tenantId = TenantId;
        _logger.LogInformation("[UsageController] GetProject tenant={TenantId}", tenantId);
        var rollup = await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.ProjectId(tenantId), ct);
        return Ok(rollup ?? EmptyProject(tenantId));
    }

    /// <summary>GET /api/usage/board/{boardId} — board-level rollup.</summary>
    [HttpGet("board/{boardId}")]
    public async Task<IActionResult> GetBoard(string boardId, CancellationToken ct)
    {
        var tenantId = TenantId;
        _logger.LogInformation("[UsageController] GetBoard board={BoardId} tenant={TenantId}", boardId, tenantId);
        var rollup = await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.BoardId(boardId), ct);
        return Ok(rollup ?? EmptyBoard(tenantId, boardId));
    }

    /// <summary>GET /api/usage/task/{taskId} — task-level rollup.</summary>
    [HttpGet("task/{taskId}")]
    public async Task<IActionResult> GetTask(string taskId, CancellationToken ct)
    {
        var tenantId = TenantId;
        _logger.LogInformation("[UsageController] GetTask task={TaskId} tenant={TenantId}", taskId, tenantId);
        var rollup = await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.TaskId(taskId), ct);
        return Ok(rollup ?? EmptyTask(tenantId, taskId));
    }

    /// <summary>GET /api/usage/task/{taskId}/events?max=50&amp;cursor= — paginated raw usage events.</summary>
    [HttpGet("task/{taskId}/events")]
    public async Task<IActionResult> GetTaskEvents(
        string taskId,
        [FromQuery] int max = 50,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[UsageController] GetTaskEvents task={TaskId} max={Max}", taskId, max);
        var clamped = Math.Clamp(max, 1, 200);
        var events = await _cosmos.GetUsageEventsForTaskAsync(taskId, clamped, cursor, ct);
        return Ok(events);
    }

    /// <summary>GET /api/usage/pricing — current pricing catalog version and prices.</summary>
    [HttpGet("pricing")]
    public IActionResult GetPricing() =>
        Ok(new { version = _cost.CatalogVersion, prices = _cost.Prices });
}
