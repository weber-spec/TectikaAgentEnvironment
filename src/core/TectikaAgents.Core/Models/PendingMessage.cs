using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// A user steering message queued for a running task. The chat API raises a Durable external
/// event AND records one of these so the orchestrator loop drains it deterministically across
/// replay. Stored in the `pendingMessages` container, partitioned by `runId`.
/// </summary>
public class PendingMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("consumed")]
    public bool Consumed { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
