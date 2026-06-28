using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>The CLASS of a run failure. Drives the short, user-facing message (via
/// <see cref="RunFailurePresenter"/>) — deliberately coarse so the copy stays actionable and we never
/// surface a raw exception. The real, exact reason travels alongside as a separate internal string.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunFailureClass
{
    /// <summary>Workspace/sandbox/ACI/Key Vault — the run's infrastructure couldn't be brought up.</summary>
    SandboxInfra,
    /// <summary>The model/Foundry service errored (HTTP failure, empty/garbled response, missing agent).</summary>
    ModelProvider,
    /// <summary>The agent used its whole budget without converging (the per-run round cap).</summary>
    Exhaustion,
    /// <summary>A QA validator and the work it gates couldn't agree the work was acceptable within the
    /// allowed revision attempts — a stuck review loop that needs human attention, not a blind re-run.</summary>
    ReviewNotConverged,
    /// <summary>The run paused for human input and no reply arrived before the wait timeout.</summary>
    UserTimeout,
    /// <summary>Anything not otherwise classified — including failures that bubbled past the orchestrator.</summary>
    Unknown
}

/// <summary>Single source of truth for what a USER sees when a run fails: a short, class-specific
/// message plus a correlation reference (the short runId) so support/devs can pivot to App Insights
/// (filter customDimensions.RunId). It maps purely from the class and never echoes the raw exception.</summary>
public static class RunFailurePresenter
{
    /// <summary>The short, user-facing message for a failure class, with the correlation ref appended.</summary>
    public static string UserMessage(RunFailureClass cls, string runId) =>
        $"{Body(cls)} (Ref: {ShortRef(runId)})";

    private static string Body(RunFailureClass cls) => cls switch
    {
        RunFailureClass.SandboxInfra =>
            "The agent's secure workspace couldn't be started, so the run stopped. " +
            "This is usually a temporary infrastructure issue — please try the task again.",
        RunFailureClass.ModelProvider =>
            "The AI model service returned an error and the run couldn't continue. " +
            "This is usually transient — please try again shortly.",
        RunFailureClass.Exhaustion =>
            "The agent stopped before finishing — it used all of its allowed steps without reaching a result. " +
            "Review the partial output, refine the task, and re-run.",
        RunFailureClass.ReviewNotConverged =>
            "A reviewer agent and the agent doing the work couldn't agree the work met the requirements, so the " +
            "task was paused for manual review. Re-running as-is will likely repeat the cycle — adjust the task " +
            "or its acceptance criteria first.",
        RunFailureClass.UserTimeout =>
            "The run was waiting for your input and timed out before a reply arrived. " +
            "Re-run the task to continue.",
        _ =>
            "The run failed unexpectedly. Use the reference below to investigate.",
    };

    /// <summary>First 8 chars of the runId (or the whole thing if shorter) — short enough to quote,
    /// long enough to locate the run. The full runId rides on the RunEvent for an exact lookup.</summary>
    private static string ShortRef(string runId) =>
        string.IsNullOrEmpty(runId) ? "unknown" : runId[..Math.Min(8, runId.Length)];

    /// <summary>Best-effort classification from a raw reason string. Only a FALLBACK for when no explicit
    /// class was carried from the throw site — known paths always pass the class directly.</summary>
    public static RunFailureClass Classify(string? internalReason)
    {
        if (string.IsNullOrWhiteSpace(internalReason)) return RunFailureClass.Unknown;
        var r = internalReason.ToLowerInvariant();

        if (r.Contains("did not converge"))
            return RunFailureClass.ReviewNotConverged;
        if (r.Contains("maximum") || r.Contains("rounds"))
            return RunFailureClass.Exhaustion;
        if (r.Contains("no response received") || r.Contains("wait timeout"))
            return RunFailureClass.UserTimeout;
        if (r.Contains("key vault") || r.Contains("keyvault") || r.Contains("workspace") ||
            r.Contains("sandbox") || r.Contains("aci") || r.Contains("did not become"))
            return RunFailureClass.SandboxInfra;
        if (r.Contains("foundry") || r.Contains("model"))
            return RunFailureClass.ModelProvider;

        return RunFailureClass.Unknown;
    }
}
