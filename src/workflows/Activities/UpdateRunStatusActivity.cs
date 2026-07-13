using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Scheduling;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class UpdateRunStatusActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<UpdateRunStatusActivity> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly IGitHubReadService _ghRead;
    private readonly IGitHubWriteService _ghWrite;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSnapshotStore _snapshots;

    public UpdateRunStatusActivity(
        WorkflowCosmosService cosmos,
        WorkflowEventPublisher events,
        ILogger<UpdateRunStatusActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IGitHubReadService ghRead,
        IGitHubWriteService ghWrite,
        IWorkspaceService workspace,
        IWorkspaceSnapshotStore snapshots)
    {
        _cosmos = cosmos;
        _events = events;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _ghRead = ghRead;
        _ghWrite = ghWrite;
        _workspace = workspace;
        _snapshots = snapshots;
    }

    [Function(nameof(UpdateRunStatusActivity))]
    public async Task Run([ActivityTrigger] UpdateRunStatusInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        _logger.LogInformation("[UpdateRunStatus] run={RunId} task={TaskId} -> {Status} step={Step}",
            input.RunId, input.TaskId, input.Status, input.CurrentStep);

        if (input.Status == RunStatus.Failed)
        {
            _logger.LogError("[UpdateRunStatus] run={RunId} task={TaskId} -> Failed: {Error}", input.RunId, input.TaskId, input.ErrorMessage ?? "(no error message found)");
        }

        var run = await _cosmos.GetRunAsync(input.TaskId, input.RunId, ct);
        if (run is null)
        {
            _logger.LogWarning("[UpdateRunStatus] run {RunId} not found — skipping status update", input.RunId);
            return;
        }

        run.Status = input.Status;
        if (input.CurrentStep.HasValue) run.CurrentStep = input.CurrentStep.Value;

        if (input.Status == RunStatus.Completed || input.Status == RunStatus.Failed)
            run.CompletedAt = DateTimeOffset.UtcNow;

        await _cosmos.UpdateRunAsync(run, ct);

        // Mirror status on the task
        var taskStatus = input.Status switch
        {
            RunStatus.Running            => AgentTaskStatus.InProgress,
            RunStatus.AwaitingInteraction => AgentTaskStatus.AwaitingInteraction,
            RunStatus.Completed          => AgentTaskStatus.Done,
            RunStatus.Failed             => AgentTaskStatus.Failed,
            RunStatus.NeedsRevision      => AgentTaskStatus.Review,
            _                            => AgentTaskStatus.InProgress
        };

        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, taskStatus, input.RunId, ct);

        if (taskStatus is AgentTaskStatus.Done or AgentTaskStatus.Failed)
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, "", ct);

        if (input.Status == RunStatus.Completed)
            await _events.PublishRunCompletedAsync(input.RunId, input.TaskId, ct);
        else if (input.Status == RunStatus.Failed)
            await EmitRunFailureAsync(input.RunId, input.TaskId, run.CurrentStep, input.FailureClass, input.ErrorMessage, ct);

        if (taskStatus == AgentTaskStatus.Done)
        {
            await ResetQaEdgeIterationsAsync(input.BoardId, input.TaskId, ct);
            // Integrate this run's branch into the repo's default branch BEFORE cascading downstream,
            // so the next run (which forks from the default branch) physically sees this task's work.
            var integrated = await MergeCompletedBranchToBaseAsync(run, input.BoardId, input.TaskId, input.RunId, ct);
            if (integrated)
                await TriggerDownstreamRunsAsync(input.BoardId, input.TaskId, ct);
        }
        else if (taskStatus == AgentTaskStatus.Review)
            await TryTriggerQaLoopAsync(input.BoardId, input.TaskId, input.RunId, ct);
    }

    /// <summary>The single place a run failure becomes user-visible: persist a <see cref="RunEventKind.RunFailed"/>
    /// event (so the Activity timeline + task banner show WHY), mirror it over SSE, and publish the run_failed
    /// AgentEvent carrying the SHORT user-facing message (which drives the failure notification). The exact
    /// internal reason and the failure class are logged with the runId for App Insights correlation.</summary>
    private async Task EmitRunFailureAsync(
        string runId, string taskId, int round, RunFailureClass? explicitClass, string? internalReason, CancellationToken ct)
    {
        var cls = explicitClass ?? RunFailurePresenter.Classify(internalReason);
        var userMessage = RunFailurePresenter.UserMessage(cls, runId);

        _logger.LogError("[RunFailure] run={RunId} task={TaskId} class={Class} reason={Reason}",
            runId, taskId, cls, internalReason ?? "(none)");

        // Persist + mirror the timeline event. Best-effort: a trace-write hiccup must not stop the
        // run_failed notification (the user still needs to be told the run failed).
        try
        {
            var ev = RunEventFactory.BuildFailureEvent(runId, taskId, round, cls, internalReason);
            var saved = await _cosmos.CreateRunEventAsync(ev, ct);
            await _events.PublishRunEventAsync(saved, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RunFailure] failed to persist/mirror RunFailed event for run {RunId}", runId);
        }

        await _events.PublishRunFailedAsync(runId, taskId, userMessage, ct);
    }

    private async Task TryTriggerQaLoopAsync(string boardId, string validatorTaskId, string runId, CancellationToken ct)
    {
        List<TaskEdge> feedbackEdges;
        try { feedbackEdges = await _cosmos.GetOutgoingQaFeedbackEdgesAsync(boardId, validatorTaskId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "[QaLoop] QaFeedback edge query failed for {TaskId}", validatorTaskId); return; }

        if (feedbackEdges.Count == 0) return;

        foreach (var edge in feedbackEdges)
            await ProcessQaFeedbackEdgeAsync(boardId, validatorTaskId, runId, edge, ct);
    }

    private async Task ProcessQaFeedbackEdgeAsync(
        string boardId, string validatorTaskId, string runId, TaskEdge edge, CancellationToken ct)
    {
        edge.CurrentIterations++;
        await _cosmos.UpdateEdgeAsync(edge, ct);

        _logger.LogInformation("[QaLoop] edge {EdgeId}: iteration {Current}/{Max}",
            edge.Id, edge.CurrentIterations, edge.MaxIterations);

        if (edge.CurrentIterations >= edge.MaxIterations)
        {
            // Exhausted: the QA loop did not converge. Don't strand the validator silently in Review —
            // mark it Blocked (needs human attention) and surface a failure so the stalled loop is visible.
            _logger.LogWarning("[QaLoop] exhausted on edge {EdgeId} after {N} iterations — marking {TaskId} Blocked",
                edge.Id, edge.CurrentIterations, validatorTaskId);
            await _cosmos.UpdateTaskStatusAsync(boardId, validatorTaskId, AgentTaskStatus.Blocked, runId, ct);
            await EmitRunFailureAsync(runId, validatorTaskId, round: 0, RunFailureClass.ReviewNotConverged,
                $"QA validation did not converge after {edge.MaxIterations} attempts — manual review required.", ct);
            return;
        }

        var loopTargetId = edge.TargetTaskId;

        List<string> segmentIds;
        try { segmentIds = await _cosmos.GetTasksBetweenAsync(boardId, loopTargetId, validatorTaskId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "[QaLoop] GetTasksBetween failed"); return; }

        await _cosmos.ResetTasksToBacklogAsync(boardId, segmentIds, ct);

        var apiBaseUrl = _config["Api:BaseUrl"];
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            _logger.LogWarning("[QaLoop] Api:BaseUrl not configured — QA retry skipped for {TaskId}", loopTargetId);
            return;
        }

        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { boardId, taskId = loopTargetId });
            var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var http = _httpClientFactory.CreateClient();
            var response = await http.PostAsync($"{apiBaseUrl.TrimEnd('/')}/api/runs/start", content, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogError("[QaLoop] retry POST returned {Status} for task {TaskId}",
                    (int)response.StatusCode, loopTargetId);
        }
        catch (Exception ex) { _logger.LogError(ex, "[QaLoop] retry trigger failed for {TaskId}", loopTargetId); }
    }

    private async Task ResetQaEdgeIterationsAsync(string boardId, string taskId, CancellationToken ct)
    {
        try
        {
            var edges = await _cosmos.GetOutgoingQaFeedbackEdgesAsync(boardId, taskId, ct);
            foreach (var edge in edges.Where(e => e.CurrentIterations > 0))
            {
                edge.CurrentIterations = 0;
                await _cosmos.UpdateEdgeAsync(edge, ct);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[QaLoop] failed to reset QA iterations on success for {TaskId}", taskId); }
    }

    /// <summary>On successful task completion, merge the run's agent branch into the repo's default branch
    /// (server-side, via the GitHub merges API) so downstream runs — which fork from the default branch —
    /// physically see this task's deliverables. Gated by the assignee role's <c>CanPushCode</c>, the single
    /// authority for any write to origin (branch push AND merge): a task whose agent lacks the permission
    /// never reaches <c>main</c>. Returns <c>true</c> if the chain may cascade downstream; <c>false</c> only
    /// when a merge conflict blocked integration (the task is sent to Review with a surfaced message).</summary>
    private async Task<bool> MergeCompletedBranchToBaseAsync(
        WorkflowRun run, string boardId, string taskId, string runId, CancellationToken ct)
    {
        AgentTask? task;
        try { task = await _cosmos.GetTaskAsync(boardId, taskId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "[Merge] task load failed for {TaskId} — skipping merge", taskId); return true; }
        if (task is null) return true;

        var board = await _cosmos.GetBoardAsync(boardId, task.TenantId, ct);
        if (board?.GitHub is null)
        {
            // No remote: deliverable files live only in this board's LOCAL workspace git. Fold the run's
            // branch into local /workspace/main so downstream runs (which fork from it) physically see
            // them. No CanPushCode gate — there is no origin to write to; this is purely local.
            return await MergeLocalNoRepoAsync(run, boardId, taskId, runId, ct);
        }

        // Enforcement: only a task whose agent role has CanPushCode may write to origin. A task without the
        // permission never pushed a branch and must not be merged to the default branch either.
        var role = task.Assignee.Type == AssigneeType.Agent && !string.IsNullOrEmpty(task.Assignee.Id)
            ? await _cosmos.GetAgentRoleAsync(task.TenantId, task.Assignee.Id, ct)
            : null;
        if (role is null || !role.Permissions.CanPushCode)
        {
            _logger.LogInformation("[Merge] task {TaskId} agent lacks CanPushCode — work stays on its branch, not merged to base", taskId);
            return true;
        }

        var head = $"agent/{runId[..Math.Min(8, runId.Length)]}";
        string baseBranch;
        try { baseBranch = (await _ghRead.GetRepoMetadataAsync(board.GitHub, ct)).DefaultBranch; }
        catch (Exception ex) { _logger.LogError(ex, "[Merge] could not resolve default branch for board {BoardId} — skipping merge", boardId); return true; }

        try
        {
            var msg = $"Merge {head} into {baseBranch} (task {taskId[..Math.Min(8, taskId.Length)]})";
            var result = await _ghWrite.MergeAsync(board.GitHub, baseBranch, head, msg, ct);
            switch (result.Outcome)
            {
                case MergeOutcome.Merged:
                    _logger.LogInformation("[Merge] {Head} → {Base} merged ({Sha}) for task {TaskId}", head, baseBranch, result.Sha, taskId);
                    return true;
                case MergeOutcome.AlreadyUpToDate:
                    _logger.LogInformation("[Merge] {Base} already contains {Head} (task {TaskId}) — nothing to merge", baseBranch, head, taskId);
                    return true;
                case MergeOutcome.Conflict:
                default:
                    _logger.LogWarning("[Merge] conflict merging {Head} → {Base} for task {TaskId} — sending to Review", head, baseBranch, taskId);
                    await MarkMergeConflictReviewAsync(boardId, taskId, runId, run.CurrentStep,
                        new[] { $"{head} ↔ {baseBranch}" }, ct);
                    return false;
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a transient GitHub error must not strand the pipeline. Log loudly and let the
            // cascade proceed; if the work truly didn't integrate, downstream surfaces it as before.
            _logger.LogError(ex, "[Merge] unexpected error merging {Head} → {Base} for task {TaskId}", head, baseBranch, taskId);
            return true;
        }
    }

    /// <summary>No-repo boards: fold the run's branch into local /workspace/main via the executor's
    /// /worktree/merge op (the local analogue of the GitHub-API merge). Returns true to allow the downstream
    /// cascade; false on a real conflict (task → Review, message surfaced, main untouched).</summary>
    private async Task<bool> MergeLocalNoRepoAsync(WorkflowRun run, string boardId, string taskId, string runId, CancellationToken ct)
    {
        if (run.WorkspaceEndpoint is null || run.WorkspaceToken is null)
        {
            _logger.LogDebug("[Merge] no repo + no workspace for task {TaskId} — nothing to merge", taskId);
            return true;   // pure-document run; nothing on disk to integrate
        }
        var runIdShort = runId[..Math.Min(8, runId.Length)];
        WorkspaceMergeResult result;
        try { result = await _workspace.MergeRunBranchAsync(run.WorkspaceEndpoint, run.WorkspaceToken, runIdShort, ct); }
        catch (Exception ex)
        {
            // Best-effort, mirroring the connected path: a transient executor error must not strand the pipeline.
            _logger.LogError(ex, "[Merge] local merge errored for task {TaskId} — allowing cascade", taskId);
            return true;
        }
        if (result.Ok)
        {
            _logger.LogInformation("[Merge] local merge of {Head} into board main ok for task {TaskId}", runIdShort, taskId);
            // Durably snapshot the advanced main line so the board's files survive ACI recycle (no-repo has
            // no origin to push to — blob storage is its durable backing store).
            try
            {
                var bundle = await _workspace.BundleAsync(run.WorkspaceEndpoint, run.WorkspaceToken, ct);
                await _snapshots.UploadAsync(boardId, bundle, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Merge] snapshot upload failed for board {BoardId} (non-fatal)", boardId);
            }
            return true;
        }
        _logger.LogWarning("[Merge] local merge conflict for task {TaskId} in: {Files}", taskId, string.Join(", ", result.ConflictFiles));
        await MarkMergeConflictReviewAsync(boardId, taskId, runId, run.CurrentStep, result.ConflictFiles, ct);
        return false;
    }

    /// <summary>A merge (server-side or local) hit a conflict: route the task to Review with a clear,
    /// user-visible message rather than silently completing. NeedsRevision → Review; an implementation task
    /// has no outgoing QaFeedback edge, so this does not bounce the upstream QA loop.</summary>
    private async Task MarkMergeConflictReviewAsync(string boardId, string taskId, string runId, int round,
        IReadOnlyList<string> conflictFiles, CancellationToken ct)
    {
        var where = conflictFiles.Count > 0 ? string.Join(", ", conflictFiles) : "shared files";
        var message = $"This task's changes could not be merged — they conflict with concurrent work in: {where}. " +
                      "The task is in Review; re-run it to rebase on the latest, or resolve the conflict manually.";
        await _cosmos.UpdateTaskStatusAsync(boardId, taskId, AgentTaskStatus.Review, runId, ct);
        try
        {
            var ev = new RunEvent { TaskId = taskId, RunId = runId, Round = round,
                Kind = RunEventKind.RevisionRequested, Title = message, Detail = message };
            var saved = await _cosmos.CreateRunEventAsync(ev, ct);
            await _events.PublishRunEventAsync(saved, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Merge] failed to surface conflict event for {TaskId}", taskId); }
    }

    private async Task TriggerDownstreamRunsAsync(string boardId, string taskId, CancellationToken ct)
    {
        var apiBaseUrl = _config["Api:BaseUrl"];
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            _logger.LogWarning("[Downstream] Api:BaseUrl is not configured — skipping downstream cascade for task {TaskId}", taskId);
            return;
        }

        IReadOnlyList<string> downstreamIds;
        try
        {
            downstreamIds = await _cosmos.GetDownstreamTaskIdsAsync(boardId, taskId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Downstream] failed to get downstream task IDs for task {TaskId}", taskId);
            return;
        }

        foreach (var downstreamId in downstreamIds)
        {
            try
            {
                // Backlog + agent-assigned + every Dependency parent Done. Shared with the in-process
                // cascade (TasksController) and Run Board's validation, so the rule can't drift again.
                var decision = await TaskStartGate.EvaluateAsync(_cosmos, boardId, downstreamId, ct);
                if (!decision.CanStart)
                {
                    if (decision.UpstreamMissing)
                        _logger.LogWarning("[Downstream] task {DownstreamId} depends on task {UpstreamId}, which no longer exists (dangling edge) — it can never start from a cascade",
                            downstreamId, decision.BlockingUpstreamId);
                    else
                        _logger.LogDebug("[Downstream] task {DownstreamId} not eligible ({Block}) — skipping", downstreamId, decision.Block);
                    continue;
                }

                // Brief delay to allow Cosmos write propagation before the downstream task reads upstream artifacts.
                // Session consistency doesn't guarantee visibility across different client sessions.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                // All checks passed — trigger the downstream run.
                // Deliberately NOT sending respectDependencies: we just evaluated the gate here, and the
                // API would re-evaluate it in a *different* Cosmos session, where this run's Done write may
                // not be visible yet (the very reason for the delay above). A second check there would drop
                // cascades silently and non-deterministically. Leave this body alone.
                var body = JsonSerializer.Serialize(new { boardId, taskId = downstreamId });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var http = _httpClientFactory.CreateClient();
                var url = $"{apiBaseUrl.TrimEnd('/')}/api/runs/start";

                _logger.LogInformation("[Downstream] triggering downstream run for task {DownstreamId} via {Url}", downstreamId, url);
                var response = await http.PostAsync(url, content, ct);

                if (!response.IsSuccessStatusCode)
                    _logger.LogError("[Downstream] POST {Url} for downstream task {DownstreamId} returned {StatusCode}", url, downstreamId, (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Downstream] error triggering downstream run for task {DownstreamId}", downstreamId);
            }
        }
    }
}

public record UpdateRunStatusInput(
    string RunId,
    string TaskId,
    string BoardId,
    RunStatus Status,
    int? CurrentStep,
    string? ErrorMessage = null,
    RunFailureClass? FailureClass = null);   // the failure CLASS that drives the user-facing message (null on non-failure transitions)
