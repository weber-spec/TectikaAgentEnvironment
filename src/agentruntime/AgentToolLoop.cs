using System.Text.Json;
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
}

/// <summary>HTTP-free agentic loop. Drives rounds via a delegate; executes explore tools against a
/// board-scoped IProjectExplorer; records control tools for the orchestrator. No Azure dependency.</summary>
public sealed class AgentToolLoop
{
    private readonly IProjectExplorer _explorer;

    public AgentToolLoop(IProjectExplorer explorer) => _explorer = explorer;

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
                Output = result.Usage.Output + resp.Usage.Output };

            if (resp.ToolCalls is null || resp.ToolCalls.Count == 0)
            {
                result.FinalText = resp.FinalText ?? "";
                return result;
            }

            var processed = await RoundExecutor.ExecuteOneRoundAsync(resp, _explorer, onToolCall, ct);
            if (processed.RoundIntent is not null) result.RoundIntent = processed.RoundIntent;
            if (processed.BriefUpdate is not null) result.BriefUpdate = processed.BriefUpdate;
            if (processed.Control is not null)
            {
                result.Control = processed.Control;          // pause: orchestrator takes over
                return result;
            }
            pending = processed.ToolOutputs;
        }
        result.MaxRoundsHit = true;
        return result;
    }
}
