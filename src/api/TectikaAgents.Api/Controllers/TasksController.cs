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
    private readonly ICosmosDbService _cosmos;

    public TasksController(ICosmosDbService cosmos) => _cosmos = cosmos;

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
            CanvasPosition = req.CanvasPosition,
            TriggerSource = TriggerSource.Manual
        };

        var created = await _cosmos.CreateTaskAsync(task, ct);
        return CreatedAtAction(nameof(Get), new { boardId, taskId = created.Id }, created);
    }

    [HttpPut("{taskId}")]
    public async Task<IActionResult> Update(string boardId, string taskId, [FromBody] UpdateTaskRequest req, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return NotFound();

        if (req.Title is not null) task.Title = req.Title;
        if (req.Description is not null) task.Description = req.Description;
        if (req.Status is not null) task.Status = req.Status.Value;
        if (req.Priority is not null) task.Priority = req.Priority.Value;
        if (req.Assignee is not null) task.Assignee = req.Assignee;
        if (req.DueAt is not null) task.DueAt = req.DueAt.Value.UtcDateTime == default ? null : req.DueAt;
        if (req.HumanAuditorId is not null) task.HumanAuditorId = req.HumanAuditorId;
        if (req.CanvasPosition is not null) task.CanvasPosition = req.CanvasPosition;

        var updated = await _cosmos.UpdateTaskAsync(task, ct);
        return Ok(updated);
    }

    [HttpDelete("{taskId}")]
    public async Task<IActionResult> Delete(string boardId, string taskId, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return NotFound();
        await _cosmos.DeleteEdgesForTaskAsync(boardId, taskId, ct);
        await _cosmos.DeleteTaskAsync(boardId, taskId, ct);
        return NoContent();
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

}

public record CreateTaskRequest(
    string Title,
    string? Description,
    TaskPriority Priority,
    TaskAssignee Assignee,
    List<string>? Dependencies,
    CanvasPosition? CanvasPosition);

public record UpdateStatusRequest(AgentTaskStatus Status);

public record UpdateTaskRequest(
    string? Title,
    string? Description,
    AgentTaskStatus? Status,
    TaskPriority? Priority,
    TaskAssignee? Assignee,
    DateTimeOffset? DueAt,
    string? HumanAuditorId,
    CanvasPosition? CanvasPosition);
