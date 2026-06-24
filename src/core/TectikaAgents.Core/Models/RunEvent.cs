using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// One persisted entry in a task's run trace. Single source of truth for both the
/// Activity tab (hierarchical timeline) and the chat transcript. Stored in the
/// `runEvents` container, partitioned by `taskId`. The same shape is broadcast over SSE,
/// so live and stored events are identical by construction.
/// </summary>
public class RunEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>Round index within the run. Parent activities and their sub-activities share a round.</summary>
    [JsonPropertyName("round")]
    public int Round { get; set; }

    /// <summary>Null = round-level activity (a parent); set = a sub-activity nested under a round event.</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("kind")]
    public RunEventKind Kind { get; set; } = RunEventKind.Thinking;

    /// <summary>Human headline, e.g. "Gathering data about the project" (round_started) or the agent/user text.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [JsonPropertyName("toolArgsSummary")]
    public string? ToolArgsSummary { get; set; }

    [JsonPropertyName("resultSummary")]
    public string? ResultSummary { get; set; }

    [JsonPropertyName("tokenUsage")]
    public TokenUsage? TokenUsage { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunEventKind
{
    Thinking,
    RoundStarted,
    ToolCall,
    ToolResult,
    ArtifactWritten,
    UserMessage,
    AgentMessage,
    InteractionRequired,
    ApprovalRequired,
    RevisionRequested,
    RoundCompleted,
    RunCompleted,
    RunFailed
}
