using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Orchestrators;

public enum SteerableState { Running, AwaitingUser, Completed, Failed, NeedsRevision }

/// <summary>Abstraction the steerable loop drives, so the loop logic is unit-testable without the
/// Durable host. The real implementation (SteerableAgentOrchestrator) maps these onto activities +
/// WaitForExternalEvent; tests use a scripted fake.</summary>
public interface IRoundDriver
{
    /// <summary>Run one round (submit pending outputs + optional user input) and return its outcome.</summary>
    Task<RoundOutcome> RunRoundAsync(int round, string? userInput, IReadOnlyList<PriorToolOutput> pending);
    /// <summary>Block until the next user message arrives (used on AwaitUser). Returns null if the wait
    /// times out before a human responds — the loop then finalizes the run rather than blocking forever.</summary>
    Task<string?> WaitForUserMessageAsync();
    /// <summary>Return a queued steering message if one arrived since the last check, else null (non-blocking).</summary>
    string? TryDrainUserMessage();
    /// <summary>Hook for run-status updates / trace persistence at each transition.</summary>
    Task OnStateAsync(SteerableState state, RoundOutcome? last);
    /// <summary>The run ended WITHOUT the agent finalizing (round cap hit, or no human reply before the
    /// AwaitUser timeout). Persist any partial deliverables and mark the run terminally failed so work
    /// isn't silently stranded with the task stuck InProgress. <paramref name="cls"/> distinguishes the
    /// two cases so the user-facing message is accurate (Exhaustion vs UserTimeout).</summary>
    Task OnExhaustedAsync(string reason, RunFailureClass cls, RoundOutcome? last);
}

/// <summary>The fine-grained Shape-B loop: the orchestrator drives one round at a time, folding any
/// injected user messages in between rounds. Pure — all IO is behind <see cref="IRoundDriver"/>.</summary>
public static class SteerableRunCore
{
    public static async Task<SteerableState> RunLoopAsync(IRoundDriver driver, string? seed, int maxRounds)
    {
        string? userInput = seed;
        IReadOnlyList<PriorToolOutput> pending = Array.Empty<PriorToolOutput>();
        RoundOutcome? last = null;

        for (var round = 0; round < maxRounds; round++)
        {
            var outcome = await driver.RunRoundAsync(round, userInput, pending);
            last = outcome;
            if (outcome.Error is not null)
            {
                await driver.OnStateAsync(SteerableState.Failed, outcome);
                return SteerableState.Failed;
            }

            var injected = driver.TryDrainUserMessage();   // non-blocking steering drain

            switch (outcome.Kind)
            {
                case RoundKind.Final:
                    await driver.OnStateAsync(SteerableState.Completed, outcome);
                    return SteerableState.Completed;

                case RoundKind.NeedsRevision:
                    // Validator (QA) agent requested an upstream re-run. The run ends here; the status
                    // flow maps NeedsRevision → task Review → the QA feedback loop re-runs the loop target.
                    await driver.OnStateAsync(SteerableState.NeedsRevision, outcome);
                    return SteerableState.NeedsRevision;

                case RoundKind.AwaitUser:
                    await driver.OnStateAsync(SteerableState.AwaitingUser, outcome);
                    var reply = injected ?? await driver.WaitForUserMessageAsync();
                    if (reply is null)
                    {
                        // No human responded before the timeout — finalize instead of blocking forever
                        // (which would also keep the sandbox alive indefinitely).
                        await driver.OnExhaustedAsync("no response received before the wait timeout",
                            RunFailureClass.UserTimeout, outcome);
                        return SteerableState.Failed;
                    }
                    // Resume: submit any explore outputs computed alongside the control tool, plus the
                    // human's reply as the control tool's function_call_output.
                    var resume = new List<PriorToolOutput>(outcome.NextToolOutputs)
                    {
                        new(outcome.OpenControlCallId!, reply)
                    };
                    pending = resume;
                    userInput = null;
                    await driver.OnStateAsync(SteerableState.Running, null);
                    break;

                case RoundKind.Continue:
                    pending = outcome.NextToolOutputs;
                    userInput = injected;              // fold steering into the next round (null if none)
                    break;
            }
        }

        // Ran out of rounds: persist partial work + mark terminal rather than failing empty-handed.
        await driver.OnExhaustedAsync($"reached the maximum of {maxRounds} rounds without completing",
            RunFailureClass.Exhaustion, last);
        return SteerableState.Failed;
    }
}
