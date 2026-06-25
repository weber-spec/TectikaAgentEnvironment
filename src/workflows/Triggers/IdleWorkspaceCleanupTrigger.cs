using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Triggers;

/// <summary>
/// Timer trigger that runs every 5 minutes and destroys board ACI containers that have been idle
/// for more than 10 minutes with no active runs (InProgress / AwaitingInteraction / Blocked).
/// </summary>
public class IdleWorkspaceCleanupTrigger
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly WorkflowCosmosService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<IdleWorkspaceCleanupTrigger> _logger;

    public IdleWorkspaceCleanupTrigger(WorkflowCosmosService cosmos, IWorkspaceService workspace,
        ILogger<IdleWorkspaceCleanupTrigger> logger)
    {
        _cosmos = cosmos;
        _workspace = workspace;
        _logger = logger;
    }

    [Function(nameof(IdleWorkspaceCleanupTrigger))]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        var boards = await _cosmos.GetBoardsWithActiveWorkspaceAsync(ct);
        _logger.LogInformation("[IdleWorkspaceCleanup] checking {Count} boards with active workspace", boards.Count);

        foreach (var board in boards)
        {
            try
            {
                await MaybeDestroyAsync(board, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IdleWorkspaceCleanup] error processing board {BoardId}", board.Id);
            }
        }
    }

    private async Task MaybeDestroyAsync(Board board, CancellationToken ct)
    {
        var lastUsed = board.WorkspaceLastUsedAt ?? board.WorkspaceStatus switch
        {
            BoardWorkspaceStatus.Ready => DateTimeOffset.MinValue,
            _ => DateTimeOffset.UtcNow   // just provisioned — don't touch
        };

        if (DateTimeOffset.UtcNow - lastUsed < IdleTimeout)
            return;

        if (await _cosmos.HasActiveRunsForBoardAsync(board.Id, ct))
        {
            _logger.LogDebug("[IdleWorkspaceCleanup] board {BoardId} has active runs — skipping", board.Id);
            return;
        }

        _logger.LogInformation("[IdleWorkspaceCleanup] board {BoardId} idle {Minutes:F1} min — destroying container {Container}",
            board.Id, (DateTimeOffset.UtcNow - lastUsed).TotalMinutes, board.WorkspaceContainerName);

        // CAS: flip Ready → None before destroying, so concurrent runs don't try to attach to a dead container.
        try
        {
            var (freshBoard, etag) = await _cosmos.GetBoardWithEtagAsync(board.Id, board.TenantId, ct);
            if (freshBoard.WorkspaceStatus != BoardWorkspaceStatus.Ready) return; // another instance already handled it
            freshBoard.WorkspaceStatus = BoardWorkspaceStatus.None;
            freshBoard.WorkspaceContainerName = null;
            freshBoard.WorkspaceEndpoint = null;
            await _cosmos.ReplaceBoardAsync(freshBoard, etag, ct);
        }
        catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            _logger.LogDebug("[IdleWorkspaceCleanup] board {BoardId} CAS conflict — another instance handled it", board.Id);
            return;
        }

        if (!string.IsNullOrEmpty(board.WorkspaceContainerName))
            await _workspace.DestroyBoardContainerAsync(board.WorkspaceContainerName, ct);
    }
}
