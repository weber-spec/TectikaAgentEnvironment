using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

public sealed record BoardWorkspaceStatusDto(
    BoardWorkspaceStatus Status,
    WorkspaceAzureState AzureState,
    string? ContainerName,
    string? Endpoint,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? IdleShutdownAt,
    bool HasActiveRuns,
    string Image);

/// <summary>Board-level workspace (ACI) control surfaced in Board Settings: status, start, restart,
/// terminate. Start mirrors the run-attach contract exactly — it persists the executor token to the
/// KV secret <c>workspace-token-board-{boardId}</c> so subsequent runs can attach.</summary>
public sealed class WorkspaceControlService
{
    // Mirrors IdleWorkspaceCleanupTrigger.IdleTimeout (10 min) for the shutdown-countdown display.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private const string AcrImage = "tacragentteam.azurecr.io/agent-workspace:latest";

    private readonly ICosmosDbService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSnapshotStore _snapshots;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<WorkspaceControlService> _logger;

    public WorkspaceControlService(ICosmosDbService cosmos, IWorkspaceService workspace,
        IWorkspaceSnapshotStore snapshots, ISecretProvider secrets, ILogger<WorkspaceControlService> logger)
    {
        _cosmos = cosmos; _workspace = workspace; _snapshots = snapshots; _secrets = secrets; _logger = logger;
    }

    public async Task<BoardWorkspaceStatusDto> GetStatusAsync(Board board, CancellationToken ct = default)
    {
        var azure = string.IsNullOrEmpty(board.WorkspaceContainerName)
            ? WorkspaceAzureState.NotFound
            : await _workspace.GetBoardContainerStatusAsync(board.WorkspaceContainerName, ct);
        return new BoardWorkspaceStatusDto(
            board.WorkspaceStatus,
            azure,
            board.WorkspaceContainerName,
            board.WorkspaceEndpoint,
            board.WorkspaceLastUsedAt,
            board.WorkspaceLastUsedAt is { } t ? t + IdleTimeout : null,
            await HasActiveRunsAsync(board.Id, ct),
            AcrImage);
    }

    /// <summary>Provision the board container now (without a run) and mark it Ready. Idempotent if the
    /// board is already provisioning/ready. Persists the token to KV and restores the no-repo snapshot,
    /// so a following run attaches to a fully-seeded container.</summary>
    public async Task<BoardWorkspaceStatusDto> StartAsync(Board board, CancellationToken ct = default)
    {
        if (board.WorkspaceStatus != BoardWorkspaceStatus.None)
            return await GetStatusAsync(board, ct);

        // Claim the slot first so a concurrently-starting run sees Provisioning and waits (mirrors the
        // run path's CAS), instead of racing us to provision the same container.
        board.WorkspaceStatus = BoardWorkspaceStatus.Provisioning;
        await _cosmos.UpdateBoardAsync(board, ct);

        try
        {
            var info = await _workspace.EnsureBoardContainerAsync(board, ct)
                       ?? throw new InvalidOperationException("Workspace provisioning returned no info.");

            // CRITICAL: persist the actual EXECUTOR_TOKEN so the run-attach path can authenticate.
            await _secrets.SetSecretAsync($"workspace-token-board-{board.Id}", info.Token, ct);

            if (board.GitHub is null)
            {
                try
                {
                    var snap = await _snapshots.DownloadAsync(board.Id, ct);
                    if (snap is not null) await _workspace.RestoreAsync(info.Endpoint, info.Token, snap, ct);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[WorkspaceControl] snapshot restore failed board {BoardId} (non-fatal)", board.Id); }
            }

            board.WorkspaceContainerName = info.ContainerName;
            board.WorkspaceEndpoint = info.Endpoint;
            board.WorkspaceStatus = BoardWorkspaceStatus.Ready;
            board.WorkspaceLastUsedAt = DateTimeOffset.UtcNow;
            await _cosmos.UpdateBoardAsync(board, ct);
            return await GetStatusAsync(board, ct);
        }
        catch
        {
            // Roll back the claim so the board isn't stranded in Provisioning.
            board.WorkspaceStatus = BoardWorkspaceStatus.None;
            board.WorkspaceContainerName = null;
            board.WorkspaceEndpoint = null;
            try { await _cosmos.UpdateBoardAsync(board, ct); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>Terminate the container now. Refuses (returns false) if the board has active runs.
    /// Keeps the durable snapshot so a later Start restores the files.</summary>
    public async Task<bool> TerminateAsync(Board board, CancellationToken ct = default)
    {
        if (await HasActiveRunsAsync(board.Id, ct)) return false;
        if (!string.IsNullOrEmpty(board.WorkspaceContainerName))
            await _workspace.DestroyBoardContainerAsync(board.WorkspaceContainerName, ct);
        board.WorkspaceContainerName = null;
        board.WorkspaceEndpoint = null;
        board.WorkspaceStatus = BoardWorkspaceStatus.None;
        board.WorkspaceLastUsedAt = null;
        await _cosmos.UpdateBoardAsync(board, ct);
        return true;
    }

    /// <summary>Restart = terminate (if up) then start. Returns null if active runs blocked the terminate.</summary>
    public async Task<BoardWorkspaceStatusDto?> RestartAsync(Board board, CancellationToken ct = default)
    {
        if (!await TerminateAsync(board, ct)) return null;
        return await StartAsync(board, ct);
    }

    private async Task<bool> HasActiveRunsAsync(string boardId, CancellationToken ct)
    {
        var tasks = await _cosmos.GetTasksByBoardAsync(boardId, ct);
        return tasks.Any(t => t.Status is AgentTaskStatus.InProgress or AgentTaskStatus.AwaitingInteraction or AgentTaskStatus.Blocked);
    }
}
