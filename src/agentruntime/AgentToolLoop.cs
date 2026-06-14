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

            var outputs = new List<ToolOutput>();
            foreach (var call in resp.ToolCalls)
            {
                onToolCall(call.Name, call.ArgumentsJson);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                var args = doc.RootElement;

                switch (call.Name)
                {
                    case "round_intent":
                        result.RoundIntent = Str(args, "text");
                        outputs.Add(new(call.CallId, "ok")); break;
                    case "update_brief":
                        result.BriefUpdate = Str(args, "text");
                        outputs.Add(new(call.CallId, "ok")); break;
                    case "request_human_input":
                        result.Control = new(PendingControlKind.HumanInput, Str(args, "question"), StrArr(args, "options"));
                        return result;                       // pause: orchestrator takes over
                    case "request_approval":
                        result.Control = new(PendingControlKind.Approval, Str(args, "description"));
                        return result;
                    case "request_revision":
                        result.Control = new(PendingControlKind.Revision, Str(args, "reason"));
                        return result;
                    case "get_board_overview":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.GetBoardOverviewAsync(ct)))); break;
                    case "search_tasks":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.SearchTasksAsync(Str(args, "query"), ct)))); break;
                    case "get_task":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.GetTaskAsync(Str(args, "taskId"), ct)))); break;
                    case "get_artifact":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.GetArtifactAsync(
                            Str(args, "taskId"), IntOrNull(args, "version"), ct)))); break;
                    default:
                        outputs.Add(new(call.CallId, $"error: unknown tool '{call.Name}'")); break;
                }
            }
            pending = outputs;
        }
        result.MaxRoundsHit = true;
        return result;
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static int? IntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static IReadOnlyList<string>? StrArr(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}
