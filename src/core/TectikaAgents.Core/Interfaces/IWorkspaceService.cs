using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Manages ACI workspaces — one container per board, one git worktree per run.
/// The container lives until idle for 10 minutes; each run's worktree is created on demand
/// and removed at run teardown.</summary>
public interface IWorkspaceService
{
    /// <summary>Provision (or return the already-running) ACI container for the board.
    /// Idempotent: if the container already exists and is healthy, returns its info immediately.</summary>
    Task<WorkspaceInfo?> EnsureBoardContainerAsync(Board board, CancellationToken ct = default);

    /// <summary>Create a git worktree for a run inside the board's container.
    /// Idempotent: if the worktree already exists, returns without error.</summary>
    Task CreateWorktreeAsync(string endpoint, string token, string runId, string branch, bool canPush, CancellationToken ct = default);

    /// <summary>Remove a git worktree for a run (best-effort — errors are logged, not thrown).</summary>
    Task RemoveWorktreeAsync(string endpoint, string token, string runId, CancellationToken ct = default);

    /// <summary>No-repo boards: fold a run's branch into the LOCAL board main line via the executor
    /// /worktree/merge op (commit worktree → merge → conflict-abort). Ok on a clean merge, else the
    /// conflicting files (main left untouched).</summary>
    Task<WorkspaceMergeResult> MergeRunBranchAsync(string endpoint, string token, string runId, CancellationToken ct = default);

    /// <summary>No-repo durable snapshot: git-bundle /workspace/main and return the bytes, for a workflow
    /// activity to upload to blob storage so the board's files survive ACI destroy.</summary>
    Task<byte[]> BundleAsync(string endpoint, string token, CancellationToken ct = default);

    /// <summary>Restore /workspace/main from a previously-saved bundle (downloaded from blob) — used on a
    /// no-repo board's first run after its container was recycled.</summary>
    Task RestoreAsync(string endpoint, string token, byte[] bundle, CancellationToken ct = default);

    /// <summary>Delete the ACI container group for a board.</summary>
    Task DestroyBoardContainerAsync(string containerName, CancellationToken ct = default);

    /// <summary>Live ACI state for the board's container group, queried from Azure Resource Manager.
    /// Returns <see cref="WorkspaceAzureState.NotFound"/> when no group exists.</summary>
    Task<WorkspaceAzureState> GetBoardContainerStatusAsync(string containerName, CancellationToken ct = default);

    /// <summary>Run a shell command in the workspace container. Returns stdout/stderr/exit_code.
    /// Pass <paramref name="runId"/> (short, 8-char) to execute inside the run's worktree;
    /// null falls back to /workspace/main.</summary>
    Task<CommandResult> RunCommandAsync(string endpoint, string token, string command, int timeoutSeconds = 60, string? runId = null, CancellationToken ct = default);

    /// <summary>POST to a named executor endpoint (e.g. "/read", "/write"). Returns raw JSON response string.</summary>
    Task<string> InvokeAsync(string endpoint, string token, string route, object body, CancellationToken ct = default);
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

public enum WorkspaceAzureState { NotFound, Provisioning, Running, Stopped, Failed, Unknown }
