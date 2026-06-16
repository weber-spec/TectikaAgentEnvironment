using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Orchestrators;

/// <summary>
/// Durable orchestrator for a steerable single-task run (fine-grained Shape B). Thin adapter: it owns
/// the round loop via <see cref="SteerableRunCore"/>, mapping each round onto <see cref="RunAgentRoundActivity"/>
/// and draining the "user_message" external event between rounds. All side effects live in activities,
/// so this stays replay-safe.
/// </summary>
public class SteerableAgentOrchestrator
{
    /// <summary>Hard cap on rounds per run (bounds cost / runaway loops).</summary>
    private const int MaxRounds = 24;

    [Function(nameof(SteerableAgentOrchestrator))]
    public async Task<SteerableRunResult> Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<SteerableAgentOrchestrator>();
        var input = context.GetInput<SteerableRunInput>()!;
        logger.LogInformation("[Steerable] start task={TaskId} run={RunId}", input.TaskId, input.RunId);

        // Provision an ACI workspace (if the board has a GitHub connection).
        WorkspaceActivityResult? workspace = null;
        try
        {
            workspace = await context.CallActivityAsync<WorkspaceActivityResult?>(
                nameof(ProvisionWorkspaceActivity),
                new ProvisionWorkspaceInput(input.RunId, input.BoardId, input.TenantId));
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Steerable] workspace provision failed (continuing without): {Msg}", ex.Message);
        }

        try
        {
            var driver = new DurableRoundDriver(context, input, workspace);
            var state = await SteerableRunCore.RunLoopAsync(driver, input.SeedMessage, MaxRounds);
            logger.LogInformation("[Steerable] done task={TaskId} run={RunId} state={State}", input.TaskId, input.RunId, state);
            return new SteerableRunResult(input.RunId, state.ToString());
        }
        finally
        {
            if (workspace is not null)
            {
                try
                {
                    await context.CallActivityAsync(nameof(DestroyWorkspaceActivity), workspace.ContainerName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("[Steerable] workspace destroy failed (non-fatal): {Msg}", ex.Message);
                }
            }
        }
    }

    /// <summary>Maps <see cref="IRoundDriver"/> onto the Durable context: activities + external events.</summary>
    private sealed class DurableRoundDriver : IRoundDriver
    {
        private const string UserMessageEvent = "user_message";
        private readonly TaskOrchestrationContext _ctx;
        private readonly SteerableRunInput _in;
        private readonly WorkspaceActivityResult? _workspace;
        private Task<string> _msgWait;   // single outstanding subscription; re-armed after each consume

        public DurableRoundDriver(TaskOrchestrationContext ctx, SteerableRunInput input, WorkspaceActivityResult? workspace)
        {
            _ctx = ctx;
            _in = input;
            _workspace = workspace;
            _msgWait = ctx.WaitForExternalEvent<string>(UserMessageEvent);
        }

        public async Task<RoundOutcome> RunRoundAsync(int round, string? userInput, IReadOnlyList<PriorToolOutput> pending)
        {
            var result = await _ctx.CallActivityAsync<RoundActivityResult>(
                nameof(RunAgentRoundActivity),
                new RoundActivityInput(_in.RunId, _in.TaskId, _in.BoardId, _in.TenantId, _in.AgentRoleId,
                    round, userInput, pending.ToList(),
                    WorkspaceEndpoint: _workspace?.Endpoint,
                    WorkspaceToken: _workspace?.Token));
            return result.Outcome;
        }

        public async Task<string> WaitForUserMessageAsync()
        {
            var msg = await _msgWait;
            _msgWait = _ctx.WaitForExternalEvent<string>(UserMessageEvent);   // re-arm
            return msg;
        }

        public string? TryDrainUserMessage()
        {
            if (!_msgWait.IsCompleted) return null;
            var msg = _msgWait.Result;
            _msgWait = _ctx.WaitForExternalEvent<string>(UserMessageEvent);   // re-arm
            return msg;
        }

        public async Task OnStateAsync(SteerableState state, RoundOutcome? last)
        {
            var status = state switch
            {
                SteerableState.Completed => RunStatus.Completed,
                SteerableState.Failed => RunStatus.Failed,
                SteerableState.AwaitingUser => RunStatus.AwaitingInteraction,
                _ => RunStatus.Running,
            };
            await _ctx.CallActivityAsync(nameof(UpdateRunStatusActivity),
                new UpdateRunStatusInput(_in.RunId, _in.TaskId, _in.BoardId, status, null,
                    ErrorMessage: last?.Error));
        }
    }
}

public record SteerableRunInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string AgentRoleId,
    string? SeedMessage);

public record SteerableRunResult(string RunId, string State);
