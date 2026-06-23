using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Durable Functions activity that commits the agent's workspace changes and pushes them to the task's
/// branch, so the work survives this run. Each run provisions a FRESH ACI that clones from origin, and the
/// agent rarely runs git itself — so without this, files written via write_file die with the container and
/// the next run can't see them (QA S1 §2.1 root cause). Called in the orchestrator's finally BEFORE
/// <see cref="DestroyWorkspaceActivity"/>, so it runs on every run-end path (Final, NeedsRevision,
/// round-exhaustion, await-timeout, error). request_human_input keeps the same run alive, so it correctly
/// does NOT trigger a commit mid-conversation.
///
/// Best-effort: it never throws (it must not block teardown), commits only when something is staged, and
/// always pushes — flushing any commit a prior step may have left unpushed after a transient failure.
/// Design: docs/superpowers/specs/2026-06-23-workspace-state-persistence-design.md.
/// </summary>
public class PersistWorkspaceActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<PersistWorkspaceActivity> _logger;

    public PersistWorkspaceActivity(WorkflowCosmosService cosmos, IWorkspaceService workspace, ILogger<PersistWorkspaceActivity> logger)
    {
        _cosmos = cosmos;
        _workspace = workspace;
        _logger = logger;
    }

    [Function(nameof(PersistWorkspaceActivity))]
    public async Task Run([ActivityTrigger] string runId, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        try
        {
            var run = await _cosmos.GetRunByIdAsync(runId, ct);
            if (run?.WorkspaceEndpoint is null || run.WorkspaceToken is null)
            {
                _logger.LogInformation("[PersistWorkspace] run {RunId} had no sandbox — nothing to persist", runId);
                return;
            }

            var msg = $"agent: task {Short(run.TaskId)} run {Short(runId)}";
            // Stage all; commit only if something is staged; then push (no-op when up to date, and flushes a
            // commit a prior step left unpushed). Push is disabled in the entrypoint for non-push roles, so an
            // ungated attempt simply fails here and is logged — state just won't carry for those roles.
            var cmd = "cd /workspace && git add -A && " +
                      $"(git diff --cached --quiet || git commit -m \"{msg}\") && " +
                      "git push origin HEAD";
            var res = await _workspace.RunCommandAsync(run.WorkspaceEndpoint, run.WorkspaceToken, cmd, 120, ct);
            if (res.ExitCode == 0)
                _logger.LogInformation("[PersistWorkspace] commit+push run={RunId} ok", runId);
            else
                _logger.LogWarning("[PersistWorkspace] run={RunId} non-zero exit={Exit} stderr={Stderr}", runId, res.ExitCode, res.Stderr);
        }
        catch (Exception ex)
        {
            // Never throw — this runs in the orchestrator's finally and must not block teardown.
            _logger.LogWarning(ex, "[PersistWorkspace] commit+push failed run={RunId} (non-fatal)", runId);
        }
    }

    private static string Short(string s) => string.IsNullOrEmpty(s) ? s : s[..Math.Min(8, s.Length)];
}
