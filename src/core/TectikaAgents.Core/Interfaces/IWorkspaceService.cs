using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Manages ephemeral ACI workspaces — one per run — where agents clone the git repo
/// and execute shell commands via the HTTP executor running on port 8080.</summary>
public interface IWorkspaceService
{
    /// <summary>Provision an ACI container that clones <paramref name="board"/>'s GitHub repo and
    /// checks out <paramref name="branchName"/>. Returns null when the board has no GitHub connection.</summary>
    Task<WorkspaceInfo?> ProvisionAsync(Board board, string branchName, string runId, CancellationToken ct = default);

    /// <summary>Run a shell command in the workspace container. Returns stdout/stderr/exit_code.</summary>
    Task<CommandResult> RunCommandAsync(string endpoint, string token, string command, int timeoutSeconds = 60, CancellationToken ct = default);

    /// <summary>Delete the ACI container group identified by <paramref name="containerName"/>.</summary>
    Task DestroyAsync(string containerName, CancellationToken ct = default);
}

public sealed record WorkspaceInfo(
    string ContainerName,
    string Endpoint,
    string Token);

public sealed record CommandResult(
    string Stdout,
    string Stderr,
    int ExitCode)
{
    public bool Success => ExitCode == 0;
    public string Summary => Success ? Stdout.Trim() : $"exit {ExitCode}\n{Stderr.Trim()}";
}
