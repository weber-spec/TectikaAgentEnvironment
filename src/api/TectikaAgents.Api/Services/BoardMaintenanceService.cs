using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

public sealed record ResetBoardResult(int TasksReset, int RunsCancelled, bool WorkspaceTerminated, bool RepoDisconnected);

/// <summary>Destructive board maintenance: reset (wipe produced work, keep the plan) and clone.</summary>
public sealed class BoardMaintenanceService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IChatService _chat;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSnapshotStore _snapshots;
    private readonly ILogger<BoardMaintenanceService> _logger;

    public BoardMaintenanceService(ICosmosDbService cosmos, IChatService chat, IWorkspaceService workspace,
        IWorkspaceSnapshotStore snapshots, ILogger<BoardMaintenanceService> logger)
    {
        _cosmos = cosmos; _chat = chat; _workspace = workspace; _snapshots = snapshots; _logger = logger;
    }

    /// <summary>Reset the board to a clean slate: cancel active runs, delete all produced work
    /// (artifacts, runs, events, usage, files), return every item to Backlog, and tear down the ACI.
    /// Keeps items, edges (counters reset), agent roles, and views. If <paramref name="clearRepo"/>,
    /// also disconnects the GitHub remote (the remote itself is never modified).</summary>
    public async Task<ResetBoardResult> ResetBoardAsync(Board board, bool clearRepo, CancellationToken ct = default)
    {
        _logger.LogInformation("[Reset] board {BoardId} clearRepo={ClearRepo}", board.Id, clearRepo);
        var tasks = (await _cosmos.GetTasksByBoardAsync(board.Id, ct)).ToList();

        // 1. Cancel any active runs (terminates the Durable orchestration; no-op when none).
        // StopAsync restores each task's pre-run status; the reset loop below overwrites it to Backlog.
        var cancelled = 0;
        foreach (var t in tasks.Where(t => t.WorkflowRunId is not null))
            if (await _chat.StopAsync(board.Id, t.Id, ct)) cancelled++;

        // 2 + 3. Purge produced work and reset each task to Backlog.
        foreach (var t in tasks)
        {
            await _cosmos.PurgeTaskWorkDataAsync(board.TenantId, board.Id, t.Id, ct);
            t.Status = AgentTaskStatus.Backlog;
            t.WorkflowRunId = null;
            t.CurrentArtifactId = null;
            t.TaskBrief = "";
            t.ArtifactSummary = null;
            t.FoundryThreadId = null;
            t.PendingOutputs = new();
            t.HumanAskCount = 0;
            t.ChatClearedAt = null;
            t.UsageSessionId = Guid.NewGuid().ToString();
            await _cosmos.UpdateTaskAsync(t, ct);
        }

        // Reset edge loop counters (edges themselves are kept).
        foreach (var e in await _cosmos.GetEdgesByBoardAsync(board.Id, ct))
            if (e.CurrentIterations != 0) { e.CurrentIterations = 0; await _cosmos.UpdateEdgeAsync(e, ct); }

        // 4. Tear down the workspace (ACI + durable snapshot). Fresh ACI provisions on the next run.
        var wsTerminated = false;
        if (!string.IsNullOrEmpty(board.WorkspaceContainerName))
        {
            await _workspace.DestroyBoardContainerAsync(board.WorkspaceContainerName, ct);
            wsTerminated = true;
        }
        await _snapshots.DeleteAsync(board.Id, ct);
        board.WorkspaceContainerName = null;
        board.WorkspaceEndpoint = null;
        board.WorkspaceStatus = BoardWorkspaceStatus.None;
        board.WorkspaceLastUsedAt = null;

        // 5. Optionally disconnect the repo (never touches the remote).
        var repoDisconnected = false;
        if (clearRepo && board.GitHub is not null) { board.GitHub = null; repoDisconnected = true; }

        await _cosmos.UpdateBoardAsync(board, ct);
        return new ResetBoardResult(tasks.Count, cancelled, wsTerminated, repoDisconnected);
    }
}
