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
    private readonly IRunStartService _runStart;
    private readonly IChatService _chat;

    public TasksController(ICosmosDbService cosmos, IRunStartService runStart, IChatService chat)
    {
        _cosmos = cosmos;
        _runStart = runStart;
        _chat = chat;
    }

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

    /// <summary>Persisted steerable-run trace for the Activity tab (and chat transcript) — replayable.</summary>
    [HttpGet("{taskId}/events")]
    public async Task<IActionResult> GetEvents(string boardId, string taskId, [FromQuery] int? sinceRound, CancellationToken ct) =>
        Ok(await _cosmos.GetRunEventsAsync(taskId, sinceRound, ct));

    /// <summary>Chat with a task: start a steerable run seeded with the message, or inject it into the live run.</summary>
    [HttpPost("{taskId}/chat")]
    public async Task<IActionResult> Chat(string boardId, string taskId, [FromBody] ChatRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Message text is required.");
        var result = await _chat.SendAsync(boardId, taskId, TenantId, req.Text.Trim(), ct);
        return result is null ? NotFound("Task not found or has no assigned agent.") : Ok(result);
    }

    /// <summary>/clear — reset the agent's context (new conversation, cleared brief + transcript boundary).</summary>
    [HttpPost("{taskId}/clear")]
    public async Task<IActionResult> Clear(string boardId, string taskId, CancellationToken ct) =>
        await _chat.ClearAsync(boardId, taskId, ct) ? Ok() : NotFound("Task not found.");

    /// <summary>/stop — terminate the task's active run.</summary>
    [HttpPost("{taskId}/stop")]
    public async Task<IActionResult> Stop(string boardId, string taskId, CancellationToken ct) =>
        await _chat.StopAsync(boardId, taskId, ct) ? Ok() : Ok(new { stopped = false }); // no active run → benign

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
        if (req.Prompt is not null) task.Prompt = req.Prompt;

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
        if (req.Status is AgentTaskStatus.Done or AgentTaskStatus.Failed)
            task.TaskBrief = "";
        var updated = await _cosmos.UpdateTaskAsync(task, ct);

        if (req.Status == AgentTaskStatus.Done)
            await TriggerDownstreamAsync(boardId, taskId, TenantId, ct);

        return Ok(updated);
    }

    private async System.Threading.Tasks.Task TriggerDownstreamAsync(string boardId, string completedTaskId, string tenantId, CancellationToken ct)
    {
        var allEdges = (await _cosmos.GetEdgesByBoardAsync(boardId, ct)).ToList();

        // Find tasks that have completedTaskId as a dependency
        var downstreamEdges = allEdges
            .Where(e => e.SourceTaskId == completedTaskId && e.Kind == EdgeKind.Dependency)
            .ToList();

        foreach (var edge in downstreamEdges)
        {
            var targetTaskId = edge.TargetTaskId;

            var targetTask = await _cosmos.GetTaskAsync(boardId, targetTaskId, ct);

            // Skip if not found, not Backlog, or not assigned to an agent
            if (targetTask is null) continue;
            if (targetTask.Status != AgentTaskStatus.Backlog) continue;
            if (targetTask.Assignee.Type != AssigneeType.Agent) continue;

            // Collect all upstream dependency IDs for this target task
            var upstreamIds = allEdges
                .Where(e => e.TargetTaskId == targetTaskId && e.Kind == EdgeKind.Dependency)
                .Select(e => e.SourceTaskId)
                .ToList();

            // Check that every upstream task is Done
            var allUpstreamDone = true;
            foreach (var upstreamId in upstreamIds)
            {
                var upstreamTask = await _cosmos.GetTaskAsync(boardId, upstreamId, ct);
                if (upstreamTask is null || upstreamTask.Status != AgentTaskStatus.Done)
                {
                    allUpstreamDone = false;
                    break;
                }
            }

            if (allUpstreamDone)
                await _runStart.StartAsync(boardId, targetTaskId, tenantId, ct);
        }
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
    CanvasPosition? CanvasPosition,
    string? Prompt = null);

public record ChatRequest(string Text);
