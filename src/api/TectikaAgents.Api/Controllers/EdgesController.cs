using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/boards/{boardId}/edges")]
public class EdgesController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    public EdgesController(ICosmosDbService cosmos) => _cosmos = cosmos;

    // Copy TenantId resolution from TasksController (e.g. User.FindFirst("tid")?.Value ?? "default").
    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    [HttpGet]
    public async Task<IActionResult> List(string boardId, CancellationToken ct)
        => Ok(await _cosmos.GetEdgesByBoardAsync(boardId, ct));

    public record CreateEdgeRequest(string SourceTaskId, string TargetTaskId, EdgeKind? Kind, string? Label);

    [HttpPost]
    public async Task<IActionResult> Create(string boardId, [FromBody] CreateEdgeRequest req, CancellationToken ct)
    {
        if (req.SourceTaskId == req.TargetTaskId) return BadRequest("A task cannot link to itself.");
        var src = await _cosmos.GetTaskAsync(boardId, req.SourceTaskId, ct);
        var dst = await _cosmos.GetTaskAsync(boardId, req.TargetTaskId, ct);
        if (src is null || dst is null) return NotFound("Source or target task not found.");

        var id = TaskEdge.MakeId(req.SourceTaskId, req.TargetTaskId);
        if (await _cosmos.GetEdgeAsync(boardId, id, ct) is not null) return Conflict("Edge already exists.");

        var existing = await _cosmos.GetEdgesByBoardAsync(boardId, ct);
        var kind = req.Kind ?? EdgeKindDetector.Detect(existing, req.SourceTaskId, req.TargetTaskId);
        var edge = new TaskEdge
        {
            Id = id, TenantId = TenantId, BoardId = boardId,
            SourceTaskId = req.SourceTaskId, TargetTaskId = req.TargetTaskId,
            Kind = kind, Label = req.Label,
            // MaxIterations defaults to 3 on the model; only consumed by Phase 7's QA loops.
        };
        return Ok(await _cosmos.CreateEdgeAsync(edge, ct));
    }

    public record UpdateEdgeRequest(EdgeKind? Kind, string? Label, string? Condition, int? MaxIterations);

    [HttpPut("{edgeId}")]
    public async Task<IActionResult> Update(string boardId, string edgeId, [FromBody] UpdateEdgeRequest req, CancellationToken ct)
    {
        var edge = await _cosmos.GetEdgeAsync(boardId, edgeId, ct);
        if (edge is null) return NotFound();
        if (req.Kind is not null) edge.Kind = req.Kind.Value;
        if (req.Label is not null) edge.Label = req.Label.Length == 0 ? null : req.Label;
        if (req.Condition is not null) edge.Condition = req.Condition.Length == 0 ? null : req.Condition;
        if (req.MaxIterations is not null) edge.MaxIterations = req.MaxIterations.Value;
        return Ok(await _cosmos.UpdateEdgeAsync(edge, ct));
    }

    [HttpDelete("{edgeId}")]
    public async Task<IActionResult> Delete(string boardId, string edgeId, CancellationToken ct)
    { await _cosmos.DeleteEdgeAsync(boardId, edgeId, ct); return NoContent(); }
}
