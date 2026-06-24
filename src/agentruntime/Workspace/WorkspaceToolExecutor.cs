using System.Text.Json;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime.Workspace;

/// <summary>
/// Handles all workspace tool calls by routing to the ACI executor HTTP API.
/// Supported tools: run_command, read_file, write_file, edit_file, list_dir, search_code.
/// </summary>
public sealed class WorkspaceToolExecutor
{
    private const int MaxTimeout = 300;

    private static readonly HashSet<string> HandledTools = new(StringComparer.Ordinal)
    {
        "run_command", "read_file", "write_file", "edit_file", "list_dir", "search_code"
    };

    private readonly IWorkspaceService _workspace;

    public WorkspaceToolExecutor(IWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public bool CanHandle(string toolName) => HandledTools.Contains(toolName);

    public async Task<string> ExecuteAsync(
        string toolName, JsonElement args, string endpoint, string token, string? runId, CancellationToken ct)
    {
        return toolName switch
        {
            "run_command" => await RunCommandAsync(args, endpoint, token, runId, ct),
            "read_file"   => await _workspace.InvokeAsync(endpoint, token, "/read", new {
                path    = Str(args, "path"),
                offset  = IntOrDefault(args, "offset", 0),
                limit   = IntOrDefault(args, "limit", 200),
                run_id  = runId }, ct),
            "write_file"  => await _workspace.InvokeAsync(endpoint, token, "/write", new {
                path    = Str(args, "path"),
                content = Str(args, "content"),
                run_id  = runId }, ct),
            "edit_file"   => await _workspace.InvokeAsync(endpoint, token, "/patch", new {
                path        = Str(args, "path"),
                old_string  = Str(args, "old_string"),
                new_string  = Str(args, "new_string"),
                replace_all = BoolOrDefault(args, "replace_all", false),
                run_id      = runId }, ct),
            "list_dir"    => await _workspace.InvokeAsync(endpoint, token, "/list", new {
                path   = Str(args, "path"),
                run_id = runId }, ct),
            "search_code" => await _workspace.InvokeAsync(endpoint, token, "/search", new {
                pattern = Str(args, "pattern"),
                path    = Str(args, "path"),
                glob    = Str(args, "glob"),
                run_id  = runId }, ct),
            _ => Err($"Unknown workspace tool '{toolName}'")
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> RunCommandAsync(JsonElement args, string endpoint, string token, string? runId, CancellationToken ct)
    {
        var cmd = Str(args, "cmd");
        if (string.IsNullOrWhiteSpace(cmd))
            return Err("'cmd' parameter is required");

        var timeout = Math.Min(MaxTimeout, IntOrDefault(args, "timeout", 60));
        var result = await _workspace.RunCommandAsync(endpoint, token, cmd, timeout, runId, ct);

        return JsonSerializer.Serialize(new {
            stdout    = result.Stdout,
            stderr    = result.Stderr,
            exit_code = result.ExitCode,
        });
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int IntOrDefault(JsonElement e, string p, int def) =>
        e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static bool BoolOrDefault(JsonElement e, string p, bool def)
    {
        if (!e.TryGetProperty(p, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _ => def,
        };
    }

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
