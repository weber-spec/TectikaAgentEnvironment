using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class NotificationDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    /// <summary>completed | failed | approval | agent</summary>
    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string? BoardId { get; set; }

    public string? TaskId { get; set; }

    public string? RunId { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>run_completed | run_failed | approval_required | interaction_required | agent_created | agent_deleted</summary>
    public string SourceEventType { get; set; } = string.Empty;

    /// <summary>If set, this notification targets a single user (e.g. an @-mention).
    /// Null = tenant-wide (existing behavior, visible to all).</summary>
    [JsonPropertyName("recipientUserId")]
    public string? RecipientUserId { get; set; }
}
