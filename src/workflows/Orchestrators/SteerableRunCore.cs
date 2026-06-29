using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;

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
    /// <summary>Compact the conversation (summarize → reset the Foundry thread) and return the summary brief
    /// to re-seed the next round with, or null if compaction failed/fell back to a clear (the loop then
    /// continues with whatever steering it had, and the hard round cap remains the backstop).</summary>
    Task<string?> CompactContextAsync();
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
    public static async Task<SteerableState> RunLoopAsync(IRoundDriver driver, string? seed, int maxRounds,
        int contextBudgetTokens = int.MaxValue, int loopWindow = 3)
    {
        string? userInput = seed;
        IReadOnlyList<PriorToolOutput> pending = Array.Empty<PriorToolOutput>();
        RoundOutcome? last = null;

        // Loop-detection state. These MUST stay loop-locals (not driver/static fields): the orchestrator
        // replays deterministically, and each is rebuilt from the recorded activity outcomes on replay.
        var signatures = new List<LoopProgressGuard.RoundSignature>();
        var suspicionPending = false;   // a DiagnosticPrompt was injected last round; interpret THIS outcome

        for (var round = 0; round < maxRounds; round++)
        {
            var outcome = await driver.RunRoundAsync(round, userInput, pending);
            last = outcome;
            if (outcome.Error is not null)
            {
                await driver.OnStateAsync(SteerableState.Failed, outcome);
                return SteerableState.Failed;
            }

            signatures.Add(LoopProgressGuard.Signature(outcome));
            var injected = driver.TryDrainUserMessage();   // non-blocking steering drain

            // Interpret the agent's answer to a previously-injected "you appear stuck" diagnostic prompt.
            if (suspicionPending)
            {
                suspicionPending = false;
                if (outcome.Kind == RoundKind.Final)
                {
                    // Agent stopped and reported — graceful completion. Its final text is the diagnosis and
                    // is already written as the run artifact.
                    await driver.OnStateAsync(SteerableState.Completed, outcome);
                    return SteerableState.Completed;
                }
                if (outcome.Kind == RoundKind.Continue || outcome.Kind == RoundKind.AwaitUser)
                {
                    // Agent declared itself NOT stuck and wants to keep going (or paused for a human). Clear
                    // the no-progress window and compact to make room before continuing. Compaction discards
                    // the Foundry thread, so any open control call_id from this round is moot — we re-seed the
                    // summary onto the fresh thread and continue rather than trying to answer it.
                    var brief = await driver.CompactContextAsync();
                    signatures.Clear();
                    pending = Array.Empty<PriorToolOutput>();   // thread reset ⇒ old call_ids are gone
                    userInput = brief ?? injected;              // re-seed the summary onto the fresh thread
                    await driver.OnStateAsync(SteerableState.Running, null);
                    continue;
                }
                // NeedsRevision falls through to the normal switch (a terminal, thread-independent outcome).
            }

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

                    // Smart context-budget check: react to real context pressure (the full server-side
                    // conversation token count) instead of just counting rounds.
                    if (outcome.Usage.Input >= contextBudgetTokens)
                    {
                        if (LoopProgressGuard.LooksStuck(signatures, loopWindow))
                        {
                            // Looks like a loop → graceful extraction. Inject our internal diagnostic prompt
                            // and let the agent's NEXT answer decide (Final = stop & report; Continue = not
                            // stuck → compact & continue). Preserve any human steering after the prompt.
                            userInput = LoopProgressGuard.DiagnosticPrompt + (injected is null ? "" : "\n\n" + injected);
                            suspicionPending = true;
                        }
                        else
                        {
                            // Not a loop — a legitimate long task whose context simply filled. Compact and
                            // continue with a smaller context (re-seeding the summary onto the fresh thread).
                            var brief = await driver.CompactContextAsync();
                            signatures.Clear();
                            pending = Array.Empty<PriorToolOutput>();
                            userInput = brief ?? injected;
                        }
                    }
                    break;
            }
        }

        // Ran out of rounds: persist partial work + mark terminal rather than failing empty-handed.
        await driver.OnExhaustedAsync($"reached the maximum of {maxRounds} rounds without completing",
            RunFailureClass.Exhaustion, last);
        return SteerableState.Failed;
    }
}
