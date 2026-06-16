using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Durable Functions activity that provisions an ACI workspace for a run.
/// Called at the start of SteerableAgentOrchestrator (when the board has a GitHub connection).
/// Returns null when no workspace is needed.
/// </summary>
public class ProvisionWorkspaceActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<ProvisionWorkspaceActivity> _logger;

    public ProvisionWorkspaceActivity(
        WorkflowCosmosService cosmos,
        IWorkspaceService workspace,
        ILogger<ProvisionWorkspaceActivity> logger)
    {
        _cosmos = cosmos;
        _workspace = workspace;
        _logger = logger;
    }

    [Function(nameof(ProvisionWorkspaceActivity))]
    public async Task<WorkspaceActivityResult?> Run(
        [ActivityTrigger] ProvisionWorkspaceInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        var board = await _cosmos.GetBoardAsync(input.BoardId, input.TenantId, ct)
            ?? throw new Exception($"Board '{input.BoardId}' not found");

        if (board.GitHub is null)
            return null;

        var branchName = $"agent/{input.RunId[..Math.Min(8, input.RunId.Length)]}";
        var info = await _workspace.ProvisionAsync(board, branchName, input.RunId, ct);
        if (info is null) return null;

        // Store the container name on the run document for cleanup on failure.
        await _cosmos.PatchRunWorkspaceAsync(input.RunId, info.ContainerName, ct);

        _logger.LogInformation("[ProvisionWorkspace] run={RunId} container={Container} endpoint={Endpoint}",
            input.RunId, info.ContainerName, info.Endpoint);

        return new WorkspaceActivityResult(info.ContainerName, info.Endpoint, info.Token, branchName);
    }
}

public record ProvisionWorkspaceInput(string RunId, string BoardId, string TenantId);

public record WorkspaceActivityResult(
    string ContainerName,
    string Endpoint,
    string Token,
    string BranchName);
