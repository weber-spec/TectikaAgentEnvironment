using System.Text.Json;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.AgentRuntime.Workspace;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>One tool call the model requested.</summary>
public sealed record ToolCall(string Name, string ArgumentsJson, string CallId);

/// <summary>What one Foundry round returned: either tool calls, or final text.</summary>
public sealed class RoundResponse
{
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? FinalText { get; init; }
    public TokenUsage Usage { get; init; } = new();
    public static RoundResponse Tools(IReadOnlyList<ToolCall> calls) => new() { ToolCalls = calls };
    public static RoundResponse Final(string text, TokenUsage usage) => new() { FinalText = text, Usage = usage };
}

/// <summary>One executed tool's output to submit back to the model.</summary>
public sealed record ToolOutput(string CallId, string Output);

public sealed class LoopResult
{
    public string FinalText { get; set; } = "";
    public string? RoundIntent { get; set; }
    public string? BriefUpdate { get; set; }
    public PendingControl? Control { get; set; }
    public TokenUsage Usage { get; set; } = new();
    public int Rounds { get; set; }
    public bool MaxRoundsHit { get; set; }

    /// <summary>Tool outputs that were executed but never submitted to the model because the loop
    /// exited first (control pause or max-rounds). The runtime submits these to close the calls so
    /// the reused conversation isn't left awaiting tool output. Empty on a normal final-text exit.</summary>
    public IReadOnlyList<ToolOutput> UnsubmittedOutputs { get; set; } = Array.Empty<ToolOutput>();

    /// <summary>When the loop paused on a control tool, the <c>call_id</c> of that control call —
    /// which has no output yet and would otherwise dangle in the conversation. Null otherwise.</summary>
    public string? OpenControlCallId { get; set; }
}

/// <summary>HTTP-free agentic loop. Drives rounds via a delegate; executes explore tools against a
/// board-scoped IProjectExplorer; records control tools for the orchestrator. No Azure dependency.</summary>
public sealed class AgentToolLoop
{
    private readonly IProjectExplorer _explorer;
    private readonly IGitHubToolExecutor? _gitHub;
    private readonly GitHubRepoConnection? _boardRepo;
    private readonly AgentRole? _role;
    private readonly WorkspaceToolExecutor? _workspace;
    private readonly TectikaAgents.Core.Interfaces.IWorkspaceProvider? _workspaceProvider;

    public AgentToolLoop(IProjectExplorer explorer, IGitHubToolExecutor? gitHub = null,
        GitHubRepoConnection? boardRepo = null, AgentRole? role = null,
        WorkspaceToolExecutor? workspace = null, TectikaAgents.Core.Interfaces.IWorkspaceProvider? workspaceProvider = null)
    {
        _explorer = explorer;
        _gitHub = gitHub;
        _boardRepo = boardRepo;
        _role = role;
        _workspace = workspace;
        _workspaceProvider = workspaceProvider;
    }

    public delegate Task<RoundResponse> SendRound(IReadOnlyList<ToolOutput> toolOutputs, CancellationToken ct);

    public async Task<LoopResult> RunAsync(SendRound sendRound, int maxRounds,
        Action<string, string> onToolCall, CancellationToken ct)
    {
        var result = new LoopResult();
        IReadOnlyList<ToolOutput> pending = Array.Empty<ToolOutput>();

        for (var round = 0; round < maxRounds; round++)
        {
            var resp = await sendRound(pending, ct);
            result.Rounds = round + 1;
            result.Usage = new TokenUsage {
                Input = result.Usage.Input + resp.Usage.Input,
                CachedInput = result.Usage.CachedInput + resp.Usage.CachedInput,
                Output = result.Usage.Output + resp.Usage.Output,
                Reasoning = result.Usage.Reasoning + resp.Usage.Reasoning };

            if (resp.ToolCalls is null || resp.ToolCalls.Count == 0)
            {
                result.FinalText = resp.FinalText ?? "";
                return result;
            }

            var processed = await RoundExecutor.ExecuteOneRoundAsync(resp, _explorer, onToolCall,
                _gitHub, _boardRepo, _role, _workspace, _workspaceProvider, ct);
            // NOTE: processed.OutputOps (declare/update/remove_output) is intentionally NOT propagated
            // here. Declared outputs are captured only on the steerable path (RunAgentRoundActivity via
            // FoundryAgentRuntime.RunRoundAsync). This legacy in-proc loop degrades gracefully: the
            // agent's final text still becomes the artifact. Wiring declared outputs through this path
            // is future work (see Phase B plan).
            if (processed.RoundIntent is not null) result.RoundIntent = processed.RoundIntent;
            if (processed.BriefUpdate is not null) result.BriefUpdate = processed.BriefUpdate;
            if (processed.Control is not null)
            {
                result.Control = processed.Control;          // pause: orchestrator takes over
                // This round's calls were executed but never submitted (we return before the next
                // sendRound). Surface them — plus the control call, which RoundExecutor leaves
                // outputless — so the runtime can close them and not poison the reused conversation.
                result.UnsubmittedOutputs = processed.ToolOutputs;
                result.OpenControlCallId = processed.OpenControlCallId;
                return result;
            }
            pending = processed.ToolOutputs;
        }
        result.MaxRoundsHit = true;
        // The final round's tool calls were executed into `pending` but never submitted.
        result.UnsubmittedOutputs = pending;
        return result;
    }
}
