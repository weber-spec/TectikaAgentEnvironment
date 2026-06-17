using System.Text.Json;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.AgentRuntime.Workspace;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Result of processing ONE model reply: its tool calls executed (explore) or captured
/// (control). Shared by the in-proc <see cref="AgentToolLoop"/> and the orchestrator-driven
/// <c>RunRoundAsync</c> so the per-round behaviour lives in exactly one place.</summary>
public sealed record RoundProcessResult(
    bool IsFinal,
    string? FinalText,
    IReadOnlyList<ToolOutput> ToolOutputs,        // explore (+ intent/brief ack) outputs to submit next
    string? OpenControlCallId,                    // control tool awaiting a human-sourced output
    PendingControl? Control,
    string? RoundIntent,
    string? BriefUpdate,
    IReadOnlyList<RoundToolCall> ToolCalls);      // summarised trace for RunEvents

/// <summary>Executes the tool calls of one round against the board-scoped explorer.</summary>
public static class RoundExecutor
{
    public static async Task<RoundProcessResult> ExecuteOneRoundAsync(
        RoundResponse resp, IProjectExplorer explorer, Action<string, string> onToolCall,
        IGitHubToolExecutor? gitHub, GitHubRepoConnection? boardRepo, AgentRole? role,
        WorkspaceToolExecutor? workspace, TectikaAgents.Core.Interfaces.IWorkspaceProvider? workspaceProvider,
        CancellationToken ct)
    {
        if (resp.ToolCalls is null || resp.ToolCalls.Count == 0)
            return new RoundProcessResult(true, resp.FinalText ?? "", [], null, null, null, null, []);

        var outputs = new List<ToolOutput>();
        var traced = new List<RoundToolCall>();
        string? intent = null, brief = null, openControlCallId = null;
        PendingControl? control = null;

        foreach (var call in resp.ToolCalls)
        {
            onToolCall(call.Name, call.ArgumentsJson);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var args = doc.RootElement;

            switch (call.Name)
            {
                case "round_intent":
                    intent = Str(args, "text");
                    outputs.Add(new(call.CallId, "ok"));
                    traced.Add(new("round_intent", intent, "ok")); break;
                case "update_brief":
                    brief = Str(args, "text");
                    outputs.Add(new(call.CallId, "ok"));
                    traced.Add(new("update_brief", brief, "ok")); break;
                case "request_human_input":
                    control ??= new(PendingControlKind.HumanInput, Str(args, "question"), StrArr(args, "options"));
                    openControlCallId ??= call.CallId;
                    traced.Add(new("request_human_input", Str(args, "question"), "awaiting human")); break;
                case "request_approval":
                    control ??= new(PendingControlKind.Approval, Str(args, "description"));
                    openControlCallId ??= call.CallId;
                    traced.Add(new("request_approval", Str(args, "description"), "awaiting human")); break;
                case "request_revision":
                    control ??= new(PendingControlKind.Revision, Str(args, "reason"));
                    openControlCallId ??= call.CallId;
                    traced.Add(new("request_revision", Str(args, "reason"), "revision requested")); break;
                case "get_board_overview":
                    outputs.Add(new(call.CallId, await Serialize(explorer.GetBoardOverviewAsync(ct))));
                    traced.Add(new("get_board_overview", "", Summarize(outputs[^1].Output))); break;
                case "search_tasks":
                    outputs.Add(new(call.CallId, await Serialize(explorer.SearchTasksAsync(Str(args, "query"), ct))));
                    traced.Add(new("search_tasks", Str(args, "query"), Summarize(outputs[^1].Output))); break;
                case "get_task":
                    outputs.Add(new(call.CallId, await Serialize(explorer.GetTaskAsync(Str(args, "taskId"), ct))));
                    traced.Add(new("get_task", Str(args, "taskId"), Summarize(outputs[^1].Output))); break;
                case "get_artifact":
                    outputs.Add(new(call.CallId, await Serialize(explorer.GetArtifactAsync(Str(args, "taskId"), IntOrNull(args, "version"), ct))));
                    traced.Add(new("get_artifact", Str(args, "taskId"), Summarize(outputs[^1].Output))); break;
                case "run_command":
                    var conn = workspaceProvider is null ? null : await workspaceProvider.EnsureAsync(ct);
                    if (workspace is not null && conn is not null)
                    {
                        var wsResult = await workspace.ExecuteAsync(args, conn.Endpoint, conn.Token, ct);
                        outputs.Add(new(call.CallId, wsResult));
                        traced.Add(new("run_command", Str(args, "cmd"), Summarize(wsResult)));
                    }
                    else
                    {
                        outputs.Add(new(call.CallId, """{"error":"The sandbox could not be started for this run."}"""));
                        traced.Add(new("run_command", Str(args, "cmd"), "no sandbox"));
                    }
                    break;
                default:
                    if (gitHub is not null && role is not null && gitHub.CanHandle(call.Name))
                    {
                        var ghResult = await gitHub.ExecuteAsync(call.Name, args, boardRepo, role, ct);
                        outputs.Add(new(call.CallId, ghResult));
                        traced.Add(new(call.Name, call.ArgumentsJson, Summarize(ghResult)));
                        break;
                    }
                    outputs.Add(new(call.CallId, $"error: unknown tool '{call.Name}'"));
                    traced.Add(new(call.Name, "", "unknown tool")); break;
            }
        }

        return new RoundProcessResult(false, null, outputs, openControlCallId, control, intent, brief, traced);
    }

    private static async Task<string> Serialize<T>(Task<T> task) => JsonSerializer.Serialize(await task);
    private static string Summarize(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static int? IntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static IReadOnlyList<string>? StrArr(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}
