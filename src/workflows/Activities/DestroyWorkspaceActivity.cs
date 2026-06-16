using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Durable Functions activity that tears down the ACI workspace container.
/// Always called in a finally block so it runs even if the agent failed or was cancelled.
/// </summary>
public class DestroyWorkspaceActivity
{
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<DestroyWorkspaceActivity> _logger;

    public DestroyWorkspaceActivity(IWorkspaceService workspace, ILogger<DestroyWorkspaceActivity> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    [Function(nameof(DestroyWorkspaceActivity))]
    public async Task Run([ActivityTrigger] string containerName, FunctionContext ctx)
    {
        _logger.LogInformation("[DestroyWorkspace] destroying {Container}", containerName);
        await _workspace.DestroyAsync(containerName, ctx.CancellationToken);
    }
}
