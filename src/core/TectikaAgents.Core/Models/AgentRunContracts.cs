namespace TectikaAgents.Core.Models;

/// <summary>Terminal outcome of one agent turn. RequiresApproval is reserved for a later phase.</summary>
public enum AgentRunStatus { Completed, Failed, BudgetExceeded, RequiresApproval }

/// <summary>Everything an agent turn needs. UserMessage is the assembled context (no system role).</summary>
public sealed record AgentRunRequest(
    AgentRole Role,
    AgentTask Task,
    string ThreadId,
    string UserMessage,
    int MaxCompletionTokens,
    string RunId,
    int Step);

public sealed record AgentRunOutcome(
    AgentRunStatus Status,
    string Content,
    ArtifactContentType ContentType,
    TokenUsage TokenUsage,
    string CompletionId,
    string? BriefUpdate = null,
    string? Error = null,
    string? RoundIntent = null,
    PendingControl? Control = null);

/// <summary>A control-tool the agent invoked that the orchestrator must act on.</summary>
public sealed record PendingControl(PendingControlKind Kind, string Text, IReadOnlyList<string>? Options = null);
public enum PendingControlKind { HumanInput, Approval, Revision }

/// <summary>Result of ensuring a role's Foundry agent exists/updated.</summary>
public sealed record AgentSyncResult(string? FoundryAgentId, bool Synced, string? Error = null);
