namespace TectikaAgents.Core.Models;

/// <summary>Disposition of one agent round in the steerable loop.</summary>
public enum RoundKind { Continue, Final, AwaitUser }

/// <summary>Inputs for ONE model⇄tool round. ThreadId is the Foundry conversation (carries history),
/// so only the new input + the previous round's tool outputs cross the boundary.</summary>
public sealed record RoundRequest(
    AgentRole Role,
    AgentTask Task,
    string ThreadId,
    string? UserInput,                                  // round-0 context / injected steering / human control-answer
    IReadOnlyList<PriorToolOutput> PendingToolOutputs,  // function_call_outputs to submit from the previous round
    int MaxCompletionTokens,
    string RunId,
    int Round,
    GitHubRepoConnection? BoardGitHub = null,
    string? WorkspaceEndpoint = null,
    string? WorkspaceToken = null);

/// <summary>A function_call_output to submit on the next round (call_id + the tool's result text).</summary>
public sealed record PriorToolOutput(string CallId, string Output);

/// <summary>A tool the agent invoked this round, summarised for the activity trace (RunEvent).</summary>
public sealed record RoundToolCall(string Name, string ArgsSummary, string ResultSummary);

/// <summary>Outcome of one round. On <see cref="RoundKind.AwaitUser"/>, <see cref="OpenControlCallId"/>
/// is the control tool's call_id whose function_call_output is the human's reply (submitted on resume),
/// and <see cref="NextToolOutputs"/> still carries any explore-tool outputs computed alongside it.</summary>
public sealed record RoundOutcome(
    RoundKind Kind,
    string? FinalText,
    IReadOnlyList<PriorToolOutput> NextToolOutputs,
    string? OpenControlCallId,
    PendingControl? Control,
    string? RoundIntent,
    string? BriefUpdate,
    IReadOnlyList<RoundToolCall> ToolCalls,
    TokenUsage Usage,
    string CompletionId,
    string? Error = null);
