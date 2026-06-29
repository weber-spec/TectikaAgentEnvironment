using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// A human↔human comment on a task. Two kinds share one document:
/// "note" (durable, typed, editable, shareable with the agent) and
/// "message" (flat discussion feed). Stored in the taskComments container,
/// partitioned by /taskId. kind/noteType are string discriminators (matching
/// NotificationDocument.Type) to avoid enum-casing drift with the frontend.
/// </summary>
public class TaskComment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;       // partition key

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>"note" | "message"</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    /// <summary>"decision" | "open_question" | "note" — notes only</summary>
    [JsonPropertyName("noteType")]
    public string? NoteType { get; set; }

    [JsonPropertyName("authorId")]
    public string AuthorId { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string> Mentions { get; set; } = [];

    /// <summary>emoji -> userIds</summary>
    [JsonPropertyName("reactions")]
    public Dictionary<string, List<string>> Reactions { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("editedBy")]
    public string? EditedBy { get; set; }

    [JsonPropertyName("deletedAt")]
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>D1: note is readable by the agent's read_team_notes tool (notes only).</summary>
    [JsonPropertyName("sharedWithAgent")]
    public bool SharedWithAgent { get; set; }

    [JsonPropertyName("sharedAt")]
    public DateTimeOffset? SharedAt { get; set; }

    [JsonPropertyName("sharedBy")]
    public string? SharedBy { get; set; }
}

/// <summary>Valid string discriminator values for TaskComment.</summary>
public static class CommentKinds
{
    public const string Note = "note";
    public const string Message = "message";
    public static readonly IReadOnlyList<string> All = [Note, Message];
}

public static class NoteTypes
{
    public const string Decision = "decision";
    public const string OpenQuestion = "open_question";
    public const string Note = "note";
    public static readonly IReadOnlyList<string> All = [Decision, OpenQuestion, Note];
}
