using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Activities;

/// <summary>Pure heuristic for the steerable loop's smart context check, factored out so it can be
/// unit-tested without the orchestrator. Given a rolling window of recent round outcomes, decides
/// whether the agent looks STUCK in a loop (the same tool calls repeated with no progress) — as opposed
/// to a legitimate long task that has merely filled its context budget. Mirrors the pure-static style of
/// <see cref="RepeatAskGuard"/>.</summary>
public static class LoopProgressGuard
{
    /// <summary>Internal prompt injected as the next round's user message when the loop heuristic fires and
    /// the context budget is nearly exhausted. The agent's own response is the arbiter: a Final round means
    /// it stopped and diagnosed (graceful completion); a Continue round means it declared itself not-stuck
    /// (the loop then compacts to make room and keeps going). Mirrors <see cref="RepeatAskGuard.Nudge"/>.</summary>
    public const string DiagnosticPrompt =
        "[System] You appear to be stuck — you have repeated the same actions without making progress and " +
        "your context budget is nearly exhausted. STOP now, diagnose WHY you are stuck, and report the problem " +
        "to the user in your final message (what you tried, what failed, what you need). " +
        "If you are NOT actually stuck and have a concrete next step that differs from what you just did, " +
        "say so explicitly in one sentence and continue.";

    /// <summary>Compact, comparable fingerprint of one round's observable behavior. <see cref="ToolSig"/>
    /// is the ordered "Name|ArgsSummary" join of the round's tool calls; <see cref="MadeProgress"/> is true
    /// when the round produced a declared-output edit or a brief update.</summary>
    public readonly record struct RoundSignature(string ToolSig, bool MadeProgress);

    /// <summary>Builds a <see cref="RoundSignature"/> from an outcome.</summary>
    public static RoundSignature Signature(RoundOutcome outcome)
    {
        var toolSig = string.Join(";", outcome.ToolCalls.Select(c => c.Name + "|" + c.ArgsSummary));
        var madeProgress = (outcome.OutputOps?.Count > 0) || !string.IsNullOrEmpty(outcome.BriefUpdate);
        return new RoundSignature(toolSig, madeProgress);
    }

    /// <summary>True when the last <paramref name="window"/> signatures all share the same NON-EMPTY tool
    /// signature and none of them made progress — the loop fingerprint. An empty tool signature (a pure
    /// text/thinking round) is treated as not-stuck to avoid false positives. Requires at least
    /// <paramref name="window"/> recorded signatures.</summary>
    public static bool LooksStuck(IReadOnlyList<RoundSignature> recent, int window)
    {
        if (window <= 0 || recent.Count < window) return false;

        var tail = recent.Skip(recent.Count - window).ToList();
        var first = tail[0].ToolSig;
        if (string.IsNullOrEmpty(first)) return false;

        return tail.All(s => s.ToolSig == first && !s.MadeProgress);
    }
}
