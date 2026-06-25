using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Builds the hierarchical RunEvent trace for one round: a parent `round_started` activity
/// with the round intent as its title, child `tool_call` sub-activities, and (on Final) an
/// `artifact_written` + `agent_message`. Pure — persistence/SSE happen in the activity.</summary>
public static class RunEventFactory
{
    public static IReadOnlyList<RunEvent> BuildRoundEvents(
        string runId, string taskId, int round, RoundOutcome outcome, string? artifactId)
    {
        var events = new List<RunEvent>();

        var parent = new RunEvent
        {
            TaskId = taskId,
            RunId = runId,
            Round = round,
            ParentId = null,
            Kind = RunEventKind.RoundStarted,
            Title = RoundTitle.Synthesize(outcome, round),
            TokenUsage = outcome.Usage,
        };
        events.Add(parent);

        foreach (var tc in outcome.ToolCalls)
        {
            events.Add(new RunEvent
            {
                TaskId = taskId,
                RunId = runId,
                Round = round,
                ParentId = parent.Id,
                Kind = RunEventKind.ToolCall,
                Title = tc.Name,
                ToolName = tc.Name,
                ToolArgsSummary = tc.ArgsSummary,
                ResultSummary = tc.ResultSummary,
            });
        }

        if (outcome.Kind == RoundKind.AwaitUser && outcome.Control is not null)
        {
            events.Add(new RunEvent
            {
                TaskId = taskId,
                RunId = runId,
                Round = round,
                ParentId = parent.Id,
                Kind = outcome.Control.Kind == PendingControlKind.Approval
                    ? RunEventKind.ApprovalRequired
                    : RunEventKind.InteractionRequired,
                Title = outcome.Control.Text,
                Detail = outcome.Control.Options is { Count: > 0 } ? string.Join(" | ", outcome.Control.Options) : null,
            });
        }

        // A validator (QA) agent called request_revision: the run ends and kicks the work back upstream.
        // Surface the reason as a first-class chat message so the user sees WHY without expanding the
        // collapsed request_revision tool line.
        if (outcome.Kind == RoundKind.NeedsRevision && outcome.Control is not null)
        {
            var reason = string.IsNullOrWhiteSpace(outcome.Control.Text) ? "Revision requested." : outcome.Control.Text;
            events.Add(new RunEvent
            {
                TaskId = taskId,
                RunId = runId,
                Round = round,
                ParentId = parent.Id,
                Kind = RunEventKind.RevisionRequested,
                Title = Truncate(reason, 280),
                Detail = reason,
            });
        }

        if (outcome.Kind == RoundKind.Final)
        {
            if (artifactId is not null)
                events.Add(new RunEvent
                {
                    TaskId = taskId,
                    RunId = runId,
                    Round = round,
                    ParentId = parent.Id,
                    Kind = RunEventKind.ArtifactWritten,
                    Title = "Wrote artifact",
                    ResultSummary = artifactId,
                });

            events.Add(new RunEvent
            {
                TaskId = taskId,
                RunId = runId,
                Round = round,
                ParentId = parent.Id,
                Kind = RunEventKind.AgentMessage,
                Title = Truncate(outcome.FinalText ?? "", 280),
                Detail = outcome.FinalText,
            });
        }

        return events;
    }

    /// <summary>Builds the terminal RunFailed event for the Activity timeline (and the task banner).
    /// Title is the short, class-mapped user message (with the short correlation ref); Detail carries the
    /// ACCURATE internal reason plus the full runId so a dev can pivot straight to App Insights. Pure —
    /// the activity persists it and mirrors it over SSE.</summary>
    public static RunEvent BuildFailureEvent(
        string runId, string taskId, int round, RunFailureClass cls, string? internalReason)
    {
        var reason = string.IsNullOrWhiteSpace(internalReason) ? "(no further detail captured)" : internalReason!;
        return new RunEvent
        {
            TaskId = taskId,
            RunId = runId,
            Round = round,
            ParentId = null,
            Kind = RunEventKind.RunFailed,
            Title = RunFailurePresenter.UserMessage(cls, runId),
            Detail = $"{reason}\nRun ID: {runId}",
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Produces a human-readable title for a round's RoundStarted event. Prefers the agent's
/// own round_intent; otherwise synthesizes one from the round's real activity so the Activity tab
/// never shows a bare "Round n".</summary>
public static class RoundTitle
{
    public static string Synthesize(RoundOutcome outcome, int round)
    {
        if (!string.IsNullOrWhiteSpace(outcome.RoundIntent))
            return outcome.RoundIntent!.Trim();

        if (outcome.Kind == RoundKind.Final && !string.IsNullOrWhiteSpace(outcome.FinalText))
            return Truncate(outcome.FinalText!.Trim(), 70);

        var verbs = outcome.ToolCalls
            .Select(tc => tc.Name)
            .Where(n => n is not "round_intent" and not "update_brief")
            .Distinct()
            .Take(3)
            .Select(FriendlyVerb)
            .ToList();
        if (verbs.Count > 0)
            return Capitalize(string.Join(", ", verbs));

        return $"Round {round + 1}";
    }

    private static string FriendlyVerb(string tool) => tool switch
    {
        "get_board_overview" => "read board",
        "search_tasks" => "searched board",
        "get_task" => "read task",
        "get_artifact" => "read artifact",
        _ when tool.Contains("github") || tool.Contains("branch") || tool.Contains("pull_request")
            || tool.Contains("push") || tool.Contains("commit") => "used GitHub",
        _ => tool,
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
