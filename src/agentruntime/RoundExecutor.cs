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
    IReadOnlyList<RoundToolCall> ToolCalls,        // summarised trace for RunEvents
    IReadOnlyList<OutputOp> OutputOps,             // declared-output edits this round
    string? WorkspaceUnavailable = null);          // set when a needed sandbox couldn't be provisioned → fail the run cleanly

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
            return new RoundProcessResult(true, resp.FinalText ?? "", [], null, null, null, null, [], []);

        var outputs = new List<ToolOutput>();
        var traced = new List<RoundToolCall>();
        var ops = new List<OutputOp>();
        string? intent = null, brief = null, openControlCallId = null, workspaceUnavailable = null;
        PendingControl? control = null;

        foreach (var call in resp.ToolCalls)
        {
            onToolCall(call.Name, call.ArgumentsJson);
            // Each tool call is isolated: a throw (malformed args JSON, Cosmos 429, sandbox 5xx, GitHub
            // error) is turned into an error result for THIS call so the model can recover, instead of
            // aborting the whole round and dropping every other call's output mid-flight.
            try
            {
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
                case "declare_output":
                {
                    var newId = Guid.NewGuid().ToString();
                    var declared = new Output
                    {
                        Id = newId,
                        Kind = OutputKind.Document,
                        Label = StrOrNull(args, "label"),
                        Inline = new InlineContent { ContentType = ParseContentType(StrOrNull(args, "contentType")), Content = Str(args, "content") },
                    };
                    ops.Add(new OutputOp(OutputOpKind.Declare, newId, Declared: declared));
                    outputs.Add(new(call.CallId, $"{{\"id\":\"{newId}\"}}"));
                    traced.Add(new("declare_output", declared.Label ?? "Document", $"declared {newId}"));
                    break;
                }
                case "update_output":
                {
                    var id = Str(args, "id");
                    InlineContent? inline = null;
                    var newContent = StrOrNull(args, "content");
                    if (newContent is not null)
                        inline = new InlineContent { ContentType = ParseContentType(StrOrNull(args, "contentType")), Content = newContent };
                    ops.Add(new OutputOp(OutputOpKind.Update, id, Label: StrOrNull(args, "label"), Inline: inline));
                    outputs.Add(new(call.CallId, "ok"));
                    traced.Add(new("update_output", id, "ok"));
                    break;
                }
                case "remove_output":
                {
                    var id = Str(args, "id");
                    ops.Add(new OutputOp(OutputOpKind.Remove, id));
                    outputs.Add(new(call.CallId, "ok"));
                    traced.Add(new("remove_output", id, "ok"));
                    break;
                }
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
                default:
                    if (workspace is not null && workspace.CanHandle(call.Name))
                    {
                        var wsConn = workspaceProvider is null ? null : await workspaceProvider.EnsureAsync(ct);
                        if (wsConn is not null)
                        {
                            var wsResult = await workspace.ExecuteAsync(call.Name, args, wsConn.Endpoint, wsConn.Token, ct);
                            outputs.Add(new(call.CallId, wsResult));
                            traced.Add(new(call.Name, WorkspaceArgSummary(call.Name, args), Summarize(wsResult)));
                        }
                        else if (workspaceProvider is not null)
                        {
                            // The agent was given workspace tools (a provider exists) but the sandbox could not
                            // be provisioned. It needs the workspace; rather than letting it limp on and emit a
                            // misleading artifact, signal the runtime to fail the run cleanly. Still answer the
                            // call so the conversation isn't left awaiting tool output.
                            workspaceUnavailable = "The workspace sandbox could not be started, so this run cannot use its workspace tools.";
                            outputs.Add(new(call.CallId, """{"error":"The workspace sandbox could not be started. This run will stop."}"""));
                            traced.Add(new(call.Name, WorkspaceArgSummary(call.Name, args), "sandbox unavailable — failing run"));
                        }
                        else
                        {
                            // No workspace is configured for this run at all (e.g. the compact path). Degrade:
                            // answer the call with an error so the model can continue without the workspace.
                            outputs.Add(new(call.CallId, """{"error":"No workspace is available for this run."}"""));
                            traced.Add(new(call.Name, WorkspaceArgSummary(call.Name, args), "no workspace"));
                        }
                        break;
                    }
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
            catch (Exception ex)
            {
                // Don't leak an already-captured control call_id with no output, and don't double-add for
                // control tools (which intentionally leave their call open). For every other tool, hand the
                // model a structured error so it can retry or change course.
                if (call.CallId != openControlCallId)
                    outputs.Add(new(call.CallId, JsonSerializer.Serialize(new { error = $"Tool '{call.Name}' failed: {ex.Message}" })));
                traced.Add(new(call.Name, call.ArgumentsJson, $"error: {ex.Message}"));
            }
        }

        // Scrub credential-shaped strings and cap each output before it re-enters the model context:
        // bounds token cost / context blow-up and keeps tokens (PAT, executor token) out of the model,
        // logs, and any artifact derived from the conversation.
        var safeOutputs = outputs
            .Select(o => new ToolOutput(o.CallId, CapOutput(SecretScrubber.Scrub(o.Output))))
            .ToList();

        return new RoundProcessResult(false, null, safeOutputs, openControlCallId, control, intent, brief, traced, ops, workspaceUnavailable);
    }

    /// <summary>Max characters of any single tool output submitted back to the model. Oversized output
    /// (e.g. `cat huge.log`, a multi-MB artifact) is truncated with a marker rather than blowing the
    /// context window or token budget.</summary>
    private const int MaxToolOutputChars = 48_000;

    private static string CapOutput(string s) =>
        string.IsNullOrEmpty(s) || s.Length <= MaxToolOutputChars
            ? s
            : s[..MaxToolOutputChars] + $"\n\n[... truncated {s.Length - MaxToolOutputChars} characters; refine the call (offset/limit, a narrower path, or a more specific query) to see more]";

    private static async Task<string> Serialize<T>(Task<T> task) => JsonSerializer.Serialize(await task);
    private static string Summarize(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private static string WorkspaceArgSummary(string toolName, JsonElement args) => toolName switch
    {
        "run_command"  => Workspace.WorkspaceToolExecutor.UnwrapCmd(Str(args, "cmd")),
        "read_file"    => Str(args, "path"),
        "write_file"   => Str(args, "path"),
        "edit_file"    => Str(args, "path"),
        "list_dir"     => Str(args, "path"),
        "search_code"  => Str(args, "pattern"),
        _              => ""
    };

    private static string? StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static ArtifactContentType ParseContentType(string? s) =>
        Enum.TryParse<ArtifactContentType>(s, ignoreCase: true, out var v) ? v : ArtifactContentType.Markdown;

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static int? IntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static IReadOnlyList<string>? StrArr(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}
