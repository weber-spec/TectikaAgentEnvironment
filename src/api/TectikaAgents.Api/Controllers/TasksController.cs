using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly CosmosDbService _cosmos;

    public TasksController(CosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> GetAll(string boardId, CancellationToken ct) =>
        Ok(await _cosmos.GetTasksByBoardAsync(boardId, ct));

    [HttpGet("{taskId}")]
    public async Task<IActionResult> Get(string boardId, string taskId, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPost]
    public async Task<IActionResult> Create(string boardId, [FromBody] CreateTaskRequest req, CancellationToken ct)
    {
        var task = new AgentTask
        {
            TenantId = TenantId,
            BoardId = boardId,
            Title = req.Title,
            Description = req.Description ?? string.Empty,
            Priority = req.Priority,
            Assignee = req.Assignee,
            CreatedBy = UserId,
            Dependencies = req.Dependencies ?? [],
            UpstreamTaskIds = req.UpstreamTaskIds ?? [],
            DownstreamTaskIds = req.DownstreamTaskIds ?? [],
            CanvasPosition = req.CanvasPosition,
            TriggerSource = TriggerSource.Manual
        };

        var created = await _cosmos.CreateTaskAsync(task, ct);
        return CreatedAtAction(nameof(Get), new { boardId, taskId = created.Id }, created);
    }

    [HttpPatch("{taskId}/status")]
    public async Task<IActionResult> UpdateStatus(string boardId, string taskId, [FromBody] UpdateStatusRequest req, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return NotFound();

        task.Status = req.Status;
        var updated = await _cosmos.UpdateTaskAsync(task, ct);
        return Ok(updated);
    }

    [HttpPatch("{taskId}/canvas-position")]
    public async Task<IActionResult> UpdateCanvasPosition(string boardId, string taskId, [FromBody] CanvasPosition position, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return NotFound();

        task.CanvasPosition = position;
        var updated = await _cosmos.UpdateTaskAsync(task, ct);
        return Ok(updated);
    }

    [HttpPost("{taskId}/connect")]
    public async Task<IActionResult> ConnectTasks(string boardId, string taskId, [FromBody] ConnectTaskRequest req, CancellationToken ct)
    {
        var upstream = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        var downstream = await _cosmos.GetTaskAsync(boardId, req.DownstreamTaskId, ct);

        if (upstream is null || downstream is null) return NotFound();

        if (!upstream.DownstreamTaskIds.Contains(req.DownstreamTaskId))
            upstream.DownstreamTaskIds.Add(req.DownstreamTaskId);

        if (!downstream.UpstreamTaskIds.Contains(taskId))
            downstream.UpstreamTaskIds.Add(taskId);

        await _cosmos.UpdateTaskAsync(upstream, ct);
        await _cosmos.UpdateTaskAsync(downstream, ct);

        return Ok(new { upstream = upstream.Id, downstream = downstream.Id });
    }
}

public record CreateTaskRequest(
    string Title,
    string? Description,
    TaskPriority Priority,
    TaskAssignee Assignee,
    List<string>? Dependencies,
    List<string>? UpstreamTaskIds,
    List<string>? DownstreamTaskIds,
    CanvasPosition? CanvasPosition);

public record UpdateStatusRequest(AgentTaskStatus Status);
public record ConnectTaskRequest(string DownstreamTaskId);
