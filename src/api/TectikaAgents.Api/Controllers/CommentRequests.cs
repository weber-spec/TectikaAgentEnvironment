using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

public record CreateCommentRequest(string Kind, string? NoteType, string Body, List<string>? Mentions);
public record UpdateCommentRequest(string Body, string? NoteType);
public record ReactionRequest(string Emoji);
public record ShareRequest(bool Shared);

/// <summary>List response: the task's comments plus the caller's Team-tab last-read marker (null if never read).</summary>
public record CommentsListResponse(
    [property: JsonPropertyName("comments")] IReadOnlyList<TaskComment> Comments,
    [property: JsonPropertyName("lastReadAt")] DateTimeOffset? LastReadAt);
