using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

// SSE / Service Bus event שנשלח ל-frontend בזמן אמת
public class AgentEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("step")]
    public int? Step { get; set; }

    [JsonPropertyName("agentRole")]
    public string? AgentRole { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tokenUsage")]
    public TokenUsage? TokenUsage { get; set; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }

    [JsonPropertyName("approvalId")]
    public string? ApprovalId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public static class Types
    {
        public const string RunStarted = "run_started";
        public const string StepStarted = "step_started";
        public const string AgentThinking = "agent_thinking";
        public const string ToolCall = "tool_call";
        public const string ArtifactUpdated = "artifact_updated";
        public const string ApprovalRequired = "approval_required";
        public const string StepCompleted = "step_completed";
        public const string RunCompleted = "run_completed";
        public const string RunFailed = "run_failed";
        public const string CliConnected = "cli_connected";
        public const string CliDisconnected = "cli_disconnected";
        public const string CliOutput = "cli_output";
    }
}
