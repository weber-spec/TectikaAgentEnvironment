using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Activities;

/// <summary>Pure decision for the QA S1 §2.2 repeat-ask guard, factored out of <see cref="RunAgentRoundActivity"/>
/// so it can be unit-tested without the activity's Cosmos/runtime dependencies. Given how many times a task has
/// ALREADY paused for request_human_input, decides whether the next ask should still pause or be converted into
/// autonomous continuation.</summary>
public static class RepeatAskGuard
{
    /// <summary>Firm nudge submitted as the control tool's function_call_output when the guard fires, so the
    /// model resumes immediately instead of pausing for a human again.</summary>
    public const string Nudge =
        "[Autonomy] You have already paused for human input the maximum number of times on this task, and you " +
        "have full autonomy to proceed. Do NOT ask the human again — especially not to decide implementation " +
        "details or to approve fixing your own build/runtime errors. Choose the most reasonable approach and " +
        "continue. If your current approach keeps failing, change the approach (a different library or design) " +
        "rather than asking. Proceed now.";

    /// <summary>If <paramref name="priorAskCount"/> has reached <paramref name="threshold"/>, returns a copy of
    /// <paramref name="outcome"/> converted from AwaitUser to Continue with <see cref="Nudge"/> appended as the
    /// open control call's function_call_output (and sets <paramref name="fired"/> = true). Otherwise returns the
    /// outcome unchanged. The caller should only invoke this for AwaitUser / request_human_input outcomes.</summary>
    public static RoundOutcome Apply(RoundOutcome outcome, int priorAskCount, int threshold, out bool fired)
    {
        fired = false;
        if (priorAskCount < threshold || outcome.OpenControlCallId is null)
            return outcome;

        fired = true;
        var next = new List<PriorToolOutput>(outcome.NextToolOutputs)
        {
            new(outcome.OpenControlCallId, Nudge),
        };
        return outcome with
        {
            Kind = RoundKind.Continue,
            NextToolOutputs = next,
            Control = null,
            OpenControlCallId = null,
        };
    }
}
