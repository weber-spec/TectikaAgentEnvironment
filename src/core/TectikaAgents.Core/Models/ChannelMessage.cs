using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// One message in a channel or DM. Modeled on TaskComment (mentions + reactions) and RunEvent
/// (agent provenance). Stored in the `channelMessages` container, partitioned by /channelId
/// (always queried by the parent channel; high write volume — mirrors runEvents' /taskId).
/// </summary>
public class ChannelMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = string.Empty;      // partition key

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Person email (Entra id) or agent role id.</summary>
    [JsonPropertyName("authorId")]
    public string AuthorId { get; set; } = string.Empty;

    /// <summary>"human" | "agent"</summary>
    [JsonPropertyName("authorType")]
    public string AuthorType { get; set; } = MemberTypes.Human;

    /// <summary>"message" | "agent_message" | "artifact" | "system"</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ChannelMessageKinds.Message;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string> Mentions { get; set; } = [];

    /// <summary>emoji -> memberIds (identical shape to TaskComment.Reactions).</summary>
    [JsonPropertyName("reactions")]
    public Dictionary<string, List<string>> Reactions { get; set; } = [];

    /// <summary>Slack-style thread parent (field reserved; threading wired in a later phase).</summary>
    [JsonPropertyName("threadParentId")]
    public string? ThreadParentId { get; set; }

    // ── Agent-linkage (agent_message / artifact) — provenance + idempotency ──
    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }

    /// <summary>The RunEvent id this message was mirrored from — dedupe key so the reconcile pass
    /// (read-time) and the SSE relay never double-insert. Mirrored messages derive their document id
    /// from this deterministically.</summary>
    [JsonPropertyName("sourceRunEventId")]
    public string? SourceRunEventId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("editedBy")]
    public string? EditedBy { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("deletedAt")]
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Deterministic id for a message mirrored from an agent RunEvent, so read-time reconcile
    /// and SSE relay converge on the same document (idempotent upsert).</summary>
    public static string MirroredId(string sourceRunEventId) => $"am:{sourceRunEventId}";

    /// <summary>Deterministic id for an artifact message, keyed by the artifact version id.</summary>
    public static string ArtifactMirrorId(string artifactId) => $"art:{artifactId}";
}

public static class ChannelMessageKinds
{
    public const string Message = "message";
    public const string AgentMessage = "agent_message";
    public const string Artifact = "artifact";
    public const string System = "system";
    public static readonly IReadOnlyList<string> All = [Message, AgentMessage, Artifact, System];
}
