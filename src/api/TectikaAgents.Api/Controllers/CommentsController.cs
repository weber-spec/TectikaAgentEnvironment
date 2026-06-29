using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/tasks/{taskId}/comments")]
[Authorize]
public class CommentsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly UserSettingsRepository _userSettings;
    private readonly NotificationRepository _notifications;
    private readonly ILogger<CommentsController> _logger;

    public CommentsController(
        ICosmosDbService cosmos,
        UserSettingsRepository userSettings,
        NotificationRepository notifications,
        ILogger<CommentsController> logger)
    {
        _cosmos = cosmos;
        _userSettings = userSettings;
        _notifications = notifications;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    /// <summary>Loads the task only if it exists AND belongs to the caller's tenant.</summary>
    private async Task<AgentTask?> AuthorizedTaskAsync(string boardId, string taskId, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        return task is not null && task.TenantId == TenantId ? task : null;
    }

    [HttpGet]
    public async Task<IActionResult> List(string boardId, string taskId, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null)
            return NotFound("Task not found.");
        return Ok(await _cosmos.GetCommentsByTaskAsync(taskId, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string boardId, string taskId, [FromBody] CreateCommentRequest req, CancellationToken ct)
    {
        var task = await AuthorizedTaskAsync(boardId, taskId, ct);
        if (task is null) return NotFound("Task not found.");

        var body = (req.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest("Comment body is required.");
        if (!CommentKinds.All.Contains(req.Kind)) return BadRequest("Invalid kind.");
        if (req.Kind == CommentKinds.Note && req.NoteType is not null && !NoteTypes.All.Contains(req.NoteType))
            return BadRequest("Invalid noteType.");

        var comment = new TaskComment
        {
            TaskId = taskId,
            BoardId = boardId,
            TenantId = TenantId,
            Kind = req.Kind,
            NoteType = req.Kind == CommentKinds.Note ? (req.NoteType ?? NoteTypes.Note) : null,
            AuthorId = UserId,
            Body = body,
            Mentions = (req.Mentions ?? []).Distinct().ToList(),
        };

        var created = await _cosmos.CreateCommentAsync(comment, ct);
        await NotifyMentionsAsync(created, ct);
        return Ok(created);
    }

    // Replaced with real implementation in a later task (Task 9). No-op for now.
    private Task NotifyMentionsAsync(TaskComment comment, CancellationToken ct) => Task.CompletedTask;
}
