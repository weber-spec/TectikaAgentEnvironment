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

    [JsonPropertyName("currentStep")]
    public int CurrentStep { get; set; } = 0;

    [JsonPropertyName("status")]
    public RunStatus Status { get; set; } = RunStatus.Pending;

    /// <summary>The task's status immediately before this run started, restored on stop/cancel
    /// (instead of forcing Backlog). Null for runs created before this field existed.</summary>
    [JsonPropertyName("previousTaskStatus")]
    public AgentTaskStatus? PreviousTaskStatus { get; set; }

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

    [JsonPropertyName("workspaceEndpoint")]
    public string? WorkspaceEndpoint { get; set; }

    [JsonPropertyName("workspaceToken")]
    public string? WorkspaceToken { get; set; }

    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("pullRequestNumber")]
    public int? PullRequestNumber { get; set; }
}

public class TokenUsage
{
    [JsonPropertyName("input")]
    public int Input { get; set; }

    /// <summary>Subset of <see cref="Input"/> served from cache — billed at the cached rate.</summary>
    [JsonPropertyName("cachedInput")]
    public int CachedInput { get; set; }

    [JsonPropertyName("output")]
    public int Output { get; set; }

    /// <summary>Subset of <see cref="Output"/> spent on reasoning — informational; already inside Output.</summary>
    [JsonPropertyName("reasoning")]
    public int Reasoning { get; set; }

    [JsonPropertyName("total")]
    public int Total => Input + Output;
}

public enum RunStatus { Pending, Running, AwaitingInteraction, Completed, Failed, Cancelled, NeedsRevision }
