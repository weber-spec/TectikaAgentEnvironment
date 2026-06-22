using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class AgentTask
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Backlog;

    [JsonPropertyName("priority")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [JsonPropertyName("assignee")]
    public TaskAssignee Assignee { get; set; } = new();

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    [JsonPropertyName("workflowRunId")]
    public string? WorkflowRunId { get; set; }

    [JsonPropertyName("triggerSource")]
    public TriggerSource TriggerSource { get; set; } = TriggerSource.Manual;

    [JsonPropertyName("triggerMeta")]
    public Dictionary<string, string> TriggerMeta { get; set; } = [];

    [JsonPropertyName("currentArtifactId")]
    public string? CurrentArtifactId { get; set; }

    [JsonPropertyName("canvasPosition")]
    public CanvasPosition? CanvasPosition { get; set; }

    [JsonPropertyName("humanAuditorId")]
    public string? HumanAuditorId { get; set; }

    [JsonPropertyName("taskBrief")]
    public string TaskBrief { get; set; } = "";

    [JsonPropertyName("pendingOutputs")]
    public List<Output> PendingOutputs { get; set; } = [];

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("chatClearedAt")]
    public DateTimeOffset? ChatClearedAt { get; set; }

    [JsonPropertyName("artifactSummary")]
    public string? ArtifactSummary { get; set; }

    [JsonPropertyName("foundryThreadId")]
    public string? FoundryThreadId { get; set; }

    /// <summary>Identifies the current usage session for the task. Reset (new GUID) on /clear ONLY.
    /// New usage events accrue to the task rollup's currentSession bucket keyed by this id.</summary>
    [JsonPropertyName("usageSessionId")]
    public string? UsageSessionId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("dueAt")]
    public DateTimeOffset? DueAt { get; set; }
}

public class TaskAssignee
{
    [JsonPropertyName("type")]
    public AssigneeType Type { get; set; } = AssigneeType.Agent;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public class CanvasPosition
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

public enum AgentTaskStatus { Backlog, InProgress, AwaitingInteraction, Blocked, Review, Done, Failed }
public enum TaskPriority { Critical, High, Medium, Low }
public enum AssigneeType { Agent, Human }
public enum TriggerSource { Manual, Supervisor, WebhookGitHub, WebhookJira, Schedule, CliBridge }
