using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class WorkflowRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("pipelineDefinition")]
    public List<PipelineStep> PipelineDefinition { get; set; } = [];

    [JsonPropertyName("currentStep")]
    public int CurrentStep { get; set; } = 0;

    [JsonPropertyName("status")]
    public RunStatus Status { get; set; } = RunStatus.Pending;

    /// <summary>The task's status immediately before this run started, restored on stop/cancel
    /// (instead of forcing Backlog). Null for runs created before this field existed.</summary>
    [JsonPropertyName("previousTaskStatus")]
    public AgentTaskStatus? PreviousTaskStatus { get; set; }

    [JsonPropertyName("steps")]
    public List<StepResult> Steps { get; set; } = [];

    [JsonPropertyName("durableFunctionInstanceId")]
    public string? DurableFunctionInstanceId { get; set; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("estimatedCostUsd")]
    public decimal EstimatedCostUsd { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>ACI container group name for the workspace provisioned by this run. Null when no
    /// GitHub repo is connected, or for pipeline runs that don't use the workspace.</summary>
    [JsonPropertyName("workspaceContainerName")]
    public string? WorkspaceContainerName { get; set; }
}

public class PipelineStep
{
    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("type")]
    public StepType Type { get; set; } = StepType.AgentExecution;

    [JsonPropertyName("agentRoleId")]
    public string? AgentRoleId { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("approvers")]
    public List<string> Approvers { get; set; } = [];
}

public class StepResult
{
    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("status")]
    public RunStatus Status { get; set; }

    [JsonPropertyName("foundryRunId")]
    public string? FoundryRunId { get; set; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }

    [JsonPropertyName("tokenUsage")]
    public TokenUsage TokenUsage { get; set; } = new();

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("pendingInteraction")]
    public PendingInteractionRequest? PendingInteraction { get; set; }

    [JsonPropertyName("revisionReason")]
    public string? RevisionReason { get; set; }
}

public class TokenUsage
{
    [JsonPropertyName("input")]
    public int Input { get; set; }

    [JsonPropertyName("output")]
    public int Output { get; set; }

    [JsonPropertyName("total")]
    public int Total => Input + Output;
}

public enum RunStatus { Pending, Running, PausedApproval, AwaitingInteraction, Completed, Failed, Cancelled, NeedsRevision }
public enum StepType { AgentExecution, ApprovalGate, CliBridge }
