using System.Text.Json;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime.Workspace;

/// <summary>
/// Handles the <c>run_command</c> tool call by forwarding the command to the ACI executor HTTP API.
/// </summary>
public sealed class WorkspaceToolExecutor
{
    private const string ToolName = "run_command";
    private const int MaxTimeout = 300;

    private readonly IWorkspaceService _workspace;

    public WorkspaceToolExecutor(IWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public bool CanHandle(string toolName) => toolName == ToolName;

    public async Task<string> ExecuteAsync(
        JsonElement args, string endpoint, string token, CancellationToken ct)
    {
        var cmd = Str(args, "cmd");
        if (string.IsNullOrWhiteSpace(cmd))
            return Err("'cmd' parameter is required");

        var timeout = Math.Min(MaxTimeout, IntOrDefault(args, "timeout", 60));

        var result = await _workspace.RunCommandAsync(endpoint, token, cmd, timeout, ct);

        return JsonSerializer.Serialize(new
        {
            stdout = result.Stdout,
            stderr = result.Stderr,
            exit_code = result.ExitCode,
        });
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int IntOrDefault(JsonElement e, string p, int def) =>
        e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
