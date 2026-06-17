using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Durable Functions activity that tears down the ACI workspace container.
/// Always called in a finally block so it runs even if the agent failed or was cancelled.
/// Looks up the container from the run doc and destroys only if one was lazily provisioned.
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
        var container = run?.WorkspaceContainerName;
        if (string.IsNullOrEmpty(container))
        {
            _logger.LogInformation("[DestroyWorkspace] run {RunId} had no sandbox — nothing to destroy", runId);
            return;
        }
        _logger.LogInformation("[DestroyWorkspace] destroying {Container} for run {RunId}", container, runId);
        await _workspace.DestroyAsync(container, ct);
    }
}
