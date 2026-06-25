using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Durable Functions activity that tears down the run's git worktree inside the board's container.
/// Always called in a finally block so it runs even if the agent failed or was cancelled.
/// The board-level ACI container is NOT destroyed here — it lives until idle cleanup fires.
/// </summary>
public class DestroyWorkspaceActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<DestroyWorkspaceActivity> _logger;

    public DestroyWorkspaceActivity(WorkflowCosmosService cosmos, IWorkspaceService workspace, ILogger<DestroyWorkspaceActivity> logger)
    {
        _cosmos = cosmos;
        _workspace = workspace;
        _logger = logger;
    }

    [Function(nameof(DestroyWorkspaceActivity))]
    public async Task Run([ActivityTrigger] string runId, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        var run = await _cosmos.GetRunByIdAsync(runId, ct);

        if (run?.WorkspaceEndpoint is null || run.WorkspaceToken is null)
        {
            _logger.LogInformation("[DestroyWorkspace] run {RunId} had no sandbox — nothing to destroy", runId);
            return;
        }

        var runIdShort = runId[..Math.Min(8, runId.Length)];
        _logger.LogInformation("[DestroyWorkspace] removing worktree {RunIdShort} for run {RunId}", runIdShort, runId);

        await _workspace.RemoveWorktreeAsync(run.WorkspaceEndpoint, run.WorkspaceToken, runIdShort, ct);

        if (!string.IsNullOrEmpty(run.BoardId) && !string.IsNullOrEmpty(run.TenantId))
            await _cosmos.PatchBoardWorkspaceLastUsedAsync(run.BoardId, run.TenantId, ct);
    }
}
