using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

public sealed record ResetBoardResult(int TasksReset, int RunsCancelled, bool WorkspaceTerminated, bool RepoDisconnected);

public interface IBoardMaintenanceService
{
    Task<ResetBoardResult> ResetBoardAsync(Board board, bool clearRepo, CancellationToken ct = default);
    Task<Board> CloneBoardAsync(Board source, string? name, bool includeData, string ownerId, CancellationToken ct = default);
}

/// <summary>Destructive board maintenance: reset (wipe produced work, keep the plan) and clone.</summary>
public sealed class BoardMaintenanceService : IBoardMaintenanceService
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

    /// <summary>Duplicate a board as a standalone (no-GitHub) board. Always copies items, edges, and
    /// board-scoped agent roles. With <paramref name="includeData"/>: keeps item statuses, copies each
    /// task's latest artifact, and seeds the workspace from the source's snapshot blob (if any). Without:
    /// items start in Backlog with an empty workspace.</summary>
    public async Task<Board> CloneBoardAsync(Board source, string? name, bool includeData, string ownerId, CancellationToken ct = default)
    {
        _logger.LogInformation("[Clone] board {BoardId} includeData={IncludeData}", source.Id, includeData);
        var clone = await _cosmos.CreateBoardAsync(new Board
        {
            TenantId = source.TenantId,
            Name = string.IsNullOrWhiteSpace(name) ? $"Copy of {source.Name}" : name.Trim(),
            Description = source.Description,
            OwnerId = ownerId,
            Columns = new List<string>(source.Columns),
        }, ct);

        var sourceTasks = (await _cosmos.GetTasksByBoardAsync(source.Id, ct)).ToList();
        var idMap = sourceTasks.ToDictionary(t => t.Id, _ => Guid.NewGuid().ToString());

        foreach (var st in sourceTasks)
        {
            // Runtime-state fields (WorkflowRunId, FoundryThreadId, UsageSessionId, PendingOutputs, HumanAskCount, ChatClearedAt) are intentionally omitted — they default to null/empty for a fresh copy.
            var nt = new AgentTask
            {
                Id = idMap[st.Id],
                TenantId = clone.TenantId,
                BoardId = clone.Id,
                Title = st.Title,
                Description = st.Description,
                Priority = st.Priority,
                Assignee = new TaskAssignee { Type = st.Assignee.Type, Id = st.Assignee.Id },
                CreatedBy = ownerId,
                Dependencies = st.Dependencies.Where(idMap.ContainsKey).Select(d => idMap[d]).ToList(),
                CanvasPosition = st.CanvasPosition is null ? null : new CanvasPosition { X = st.CanvasPosition.X, Y = st.CanvasPosition.Y },
                Prompt = st.Prompt,
                HumanAuditorId = st.HumanAuditorId,
                DueAt = st.DueAt,
                Status = includeData ? st.Status : AgentTaskStatus.Backlog,
            };

            if (includeData)
            {
                nt.ArtifactSummary = st.ArtifactSummary;
                nt.TaskBrief = st.TaskBrief;
                var latest = (await _cosmos.GetArtifactVersionsAsync(st.Id, ct)).FirstOrDefault();
                if (latest is not null)
                {
                    var copy = new Artifact
                    {
                        TenantId = clone.TenantId,
                        TaskId = nt.Id,
                        RunId = null,
                        Version = 1,
                        ContentType = latest.ContentType,
                        Content = latest.Content,
                        Summary = latest.Summary,
                        Outputs = latest.Outputs.Count == 0 ? new() : new List<Output>(latest.Outputs),
                        Origin = latest.Origin,
                    };
                    await _cosmos.CreateArtifactAsync(copy, ct);
                    nt.CurrentArtifactId = copy.Id;
                }
            }

            await _cosmos.CreateTaskAsync(nt, ct);
        }

        foreach (var e in await _cosmos.GetEdgesByBoardAsync(source.Id, ct))
        {
            if (!idMap.TryGetValue(e.SourceTaskId, out var ns) || !idMap.TryGetValue(e.TargetTaskId, out var nt2)) continue;
            await _cosmos.CreateEdgeAsync(new TaskEdge
            {
                Id = TaskEdge.MakeId(ns, nt2),
                TenantId = clone.TenantId,
                BoardId = clone.Id,
                SourceTaskId = ns,
                TargetTaskId = nt2,
                Kind = e.Kind,
                Label = e.Label,
                Condition = e.Condition,
                MaxIterations = e.MaxIterations,
                CurrentIterations = includeData ? e.CurrentIterations : 0,
            }, ct);
        }

        // Agent roles are tenant-scoped (AgentRole has no BoardId), so the clone already shares the
        // tenant's roles — nothing to copy.

        if (includeData)
        {
            // Seed workspace files from the source's durable snapshot (no-repo boards keep one per run).
            // A connected source's files live in its remote, not here, so its clone may start empty —
            // documented limitation; deliverables still travel as the copied artifacts above.
            var bundle = await _snapshots.DownloadAsync(source.Id, ct);
            if (bundle is not null) await _snapshots.UploadAsync(clone.Id, bundle, ct);
        }

        return clone;
    }
}
