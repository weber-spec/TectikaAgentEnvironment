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

    /// <summary>How long to wait for a human reply on AwaitUser before finalizing the run. Bounds the
    /// orchestration (and the provisioned sandbox) so an unanswered request can't hang forever.</summary>
    private static readonly TimeSpan AwaitUserTimeout = TimeSpan.FromHours(48);

    [Function(nameof(SteerableAgentOrchestrator))]
    public async Task<SteerableRunResult> Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<SteerableAgentOrchestrator>();
        var input = context.GetInput<SteerableRunInput>()!;
        logger.LogInformation("[Steerable] start task={TaskId} run={RunId}", input.TaskId, input.RunId);

        try
        {
            var driver = new DurableRoundDriver(context, input);
            var state = await SteerableRunCore.RunLoopAsync(driver, input.SeedMessage, MaxRounds);
            logger.LogInformation("[Steerable] done task={TaskId} run={RunId} state={State}", input.TaskId, input.RunId, state);
            return new SteerableRunResult(input.RunId, state.ToString());
        }
        finally
        {
            // Persist the run's workspace changes (commit+push to the task branch) BEFORE teardown, so the
            // next run's fresh clone restores them. Its own try/catch so a persist failure never blocks the
            // destroy below — leaking a billing ACI is worse than losing a best-effort commit.
            try
            {
                await context.CallActivityAsync(nameof(PersistWorkspaceActivity), input.RunId);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Steerable] workspace persist failed (non-fatal): {Msg}", ex.Message);
            }

            // Destroy the sandbox if RunAgentRoundActivity lazily provisioned one (no-op otherwise).
            try
            {
                await context.CallActivityAsync(nameof(DestroyWorkspaceActivity), input.RunId);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Steerable] workspace destroy failed (non-fatal): {Msg}", ex.Message);
            }
        }
    }

    /// <summary>Maps <see cref="IRoundDriver"/> onto the Durable context: activities + external events.</summary>
    private sealed class DurableRoundDriver : IRoundDriver
    {
        private const string UserMessageEvent = "user_message";
        private readonly TaskOrchestrationContext _ctx;
        private readonly SteerableRunInput _in;
        private Task<string> _msgWait;   // single outstanding subscription; re-armed after each consume

        public DurableRoundDriver(TaskOrchestrationContext ctx, SteerableRunInput input)
        {
            _ctx = ctx;
            _in = input;
            _msgWait = ctx.WaitForExternalEvent<string>(UserMessageEvent);
        }

        public async Task<RoundOutcome> RunRoundAsync(int round, string? userInput, IReadOnlyList<PriorToolOutput> pending)
        {
            var result = await _ctx.CallActivityAsync<RoundActivityResult>(
                nameof(RunAgentRoundActivity),
                new RoundActivityInput(_in.RunId, _in.TaskId, _in.BoardId, _in.TenantId, _in.AgentRoleId,
                    round, userInput, pending.ToList()));
            return result.Outcome;
        }

        public async Task<string?> WaitForUserMessageAsync()
        {
            // Replay-safe bounded wait: race the pending event against a durable timer. Returning null on
            // timeout lets the loop finalize the run instead of blocking the orchestration forever.
            using var cts = new CancellationTokenSource();
            var timer = _ctx.CreateTimer(_ctx.CurrentUtcDateTime.Add(AwaitUserTimeout), cts.Token);
            var winner = await Task.WhenAny(_msgWait, timer);
            if (winner != _msgWait)
                return null;   // timed out — leave _msgWait armed; a later event simply won't be consumed

            cts.Cancel();   // event arrived first — cancel the timer
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
                SteerableState.NeedsRevision => RunStatus.NeedsRevision,
                _ => RunStatus.Running,
            };
            await _ctx.CallActivityAsync(nameof(UpdateRunStatusActivity),
                new UpdateRunStatusInput(_in.RunId, _in.TaskId, _in.BoardId, status, null,
                    ErrorMessage: last?.Error));
        }

        public async Task OnExhaustedAsync(string reason, RoundOutcome? last)
        {
            // Preserve partial deliverables as a terminal artifact + mark the task Failed...
            await _ctx.CallActivityAsync(nameof(FinalizeExhaustedRunActivity),
                new FinalizeExhaustedInput(_in.RunId, _in.TaskId, _in.BoardId, _in.TenantId, reason));
            // ...then mark the run itself Failed (with the reason as the error message).
            await _ctx.CallActivityAsync(nameof(UpdateRunStatusActivity),
                new UpdateRunStatusInput(_in.RunId, _in.TaskId, _in.BoardId, RunStatus.Failed, null,
                    ErrorMessage: reason));
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
