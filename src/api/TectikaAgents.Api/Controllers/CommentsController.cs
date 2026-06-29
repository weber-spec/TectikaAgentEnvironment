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

    [HttpPut("{commentId}")]
    public async Task<IActionResult> Update(string boardId, string taskId, string commentId, [FromBody] UpdateCommentRequest req, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");
        if (comment.AuthorId != UserId) return Forbid();

        var body = (req.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest("Comment body is required.");
        if (comment.Kind == CommentKinds.Note && req.NoteType is not null && !NoteTypes.All.Contains(req.NoteType))
            return BadRequest("Invalid noteType.");

        comment.Body = body;
        if (comment.Kind == CommentKinds.Note && req.NoteType is not null) comment.NoteType = req.NoteType;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        comment.EditedBy = UserId;

        var saved = await _cosmos.UpsertCommentAsync(comment, ct);
        return Ok(saved);
    }

    [HttpDelete("{commentId}")]
    public async Task<IActionResult> Delete(string boardId, string taskId, string commentId, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");
        if (comment.AuthorId != UserId) return Forbid();

        comment.DeletedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpsertCommentAsync(comment, ct);
        return Ok(new { deleted = true });
    }

    [HttpPost("{commentId}/reactions")]
    public async Task<IActionResult> React(string boardId, string taskId, string commentId, [FromBody] ReactionRequest req, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        if (string.IsNullOrWhiteSpace(req.Emoji)) return BadRequest("Emoji is required.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");

        var users = comment.Reactions.TryGetValue(req.Emoji, out var list) ? list : new List<string>();
        if (users.Contains(UserId)) users.Remove(UserId);
        else users.Add(UserId);

        if (users.Count == 0) comment.Reactions.Remove(req.Emoji);
        else comment.Reactions[req.Emoji] = users;

        return Ok(await _cosmos.UpsertCommentAsync(comment, ct));
    }

    [HttpPost("{commentId}/share")]
    public async Task<IActionResult> Share(string boardId, string taskId, string commentId, [FromBody] ShareRequest req, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");
        if (comment.Kind != CommentKinds.Note) return BadRequest("Only notes can be shared with the agent.");

        comment.SharedWithAgent = req.Shared;
        if (req.Shared)
        {
            comment.SharedAt = DateTimeOffset.UtcNow;
            comment.SharedBy = UserId;
        }
        return Ok(await _cosmos.UpsertCommentAsync(comment, ct));
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkRead(string boardId, string taskId, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var settings = await _userSettings.GetOrCreateAsync(UserId, ct);
        var now = DateTimeOffset.UtcNow;
        settings.TaskReadMarkers[taskId] = now;
        await _userSettings.UpsertAsync(settings, ct);
        return Ok(new { lastReadAt = now });
    }

    // Replaced with real implementation in a later task (Task 9). No-op for now.
    private Task NotifyMentionsAsync(TaskComment comment, CancellationToken ct) => Task.CompletedTask;
}
