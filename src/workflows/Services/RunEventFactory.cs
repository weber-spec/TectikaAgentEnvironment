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
            Title = string.IsNullOrWhiteSpace(outcome.RoundIntent) ? $"Round {round + 1}" : outcome.RoundIntent,
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
