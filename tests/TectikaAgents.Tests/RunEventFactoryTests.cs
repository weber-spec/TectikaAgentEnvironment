using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class RunEventFactoryTests
{
    private static RoundOutcome Final(string text, params RoundToolCall[] calls) =>
        new(RoundKind.Final, text, Array.Empty<PriorToolOutput>(), null, null, "Gathering data",
            null, calls, new TokenUsage { Input = 3, Output = 2 }, "c1");

    [Fact]
    public void Final_Builds_Parent_Tools_Artifact_AndMessage()
    {
        var outcome = Final("The final answer.",
            new RoundToolCall("get_board_overview", "", "2 tasks"),
            new RoundToolCall("get_artifact", "taskId=u1", "1 KB"));

        var events = RunEventFactory.BuildRoundEvents("run-1", "task-1", round: 2, outcome, artifactId: "art-9");

        var parent = Assert.Single(events.Where(e => e.Kind == RunEventKind.RoundStarted));
        Assert.Null(parent.ParentId);
        Assert.Equal("Gathering data", parent.Title);          // intent becomes the parent title
        Assert.Equal(2, parent.Round);

        var tools = events.Where(e => e.Kind == RunEventKind.ToolCall).ToList();
        Assert.Equal(2, tools.Count);
        Assert.All(tools, t => Assert.Equal(parent.Id, t.ParentId));   // tools nested under the round

        var artifact = Assert.Single(events.Where(e => e.Kind == RunEventKind.ArtifactWritten));
        Assert.Equal("art-9", artifact.ResultSummary);
        Assert.Equal(parent.Id, artifact.ParentId);

        Assert.Single(events.Where(e => e.Kind == RunEventKind.AgentMessage));
    }

    [Fact]
    public void AwaitUser_AddsInteractionEvent()
    {
        var outcome = new RoundOutcome(RoundKind.AwaitUser, null, Array.Empty<PriorToolOutput>(),
            "call_q", new PendingControl(PendingControlKind.HumanInput, "Which hotel?", new[] { "A", "B" }),
            "Asking the user", null, Array.Empty<RoundToolCall>(), new TokenUsage(), "c2");

        var events = RunEventFactory.BuildRoundEvents("run-1", "task-1", 0, outcome, null);

        var interaction = Assert.Single(events.Where(e => e.Kind == RunEventKind.InteractionRequired));
        Assert.Equal("Which hotel?", interaction.Title);
        Assert.Equal("A | B", interaction.Detail);
        Assert.DoesNotContain(events, e => e.Kind == RunEventKind.ArtifactWritten);
    }

    [Fact]
    public void BuildFailureEvent_HasFriendlyTitle_AndAccurateDetailWithFullRunId()
    {
        const string runId = "abc1234567-run";
        var internalReason = "Key Vault secret 'workspace-token-board-x' returned 403 Forbidden";

        var ev = RunEventFactory.BuildFailureEvent(runId, "task-1", round: 5,
            RunFailureClass.SandboxInfra, internalReason);

        Assert.Equal(RunEventKind.RunFailed, ev.Kind);
        Assert.Null(ev.ParentId);                              // top-level row in the Activity timeline
        Assert.Equal(5, ev.Round);
        Assert.Equal(runId, ev.RunId);
        Assert.Equal("task-1", ev.TaskId);

        // Title = the short, user-facing message (class-specific + short correlation ref), NOT the raw error.
        Assert.Equal(RunFailurePresenter.UserMessage(RunFailureClass.SandboxInfra, runId), ev.Title);
        Assert.DoesNotContain("403", ev.Title!);

        // Detail = the accurate internal reason + the FULL runId, for devs pivoting to App Insights.
        Assert.Contains(internalReason, ev.Detail!);
        Assert.Contains(runId, ev.Detail!);
    }

    [Fact]
    public void BuildFailureEvent_ToleratesNullInternalReason()
    {
        var ev = RunEventFactory.BuildFailureEvent("run-1", "task-1", 0, RunFailureClass.Unknown, null);
        Assert.Equal(RunEventKind.RunFailed, ev.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ev.Title));    // still a friendly title
        Assert.Contains("run-1", ev.Detail!);                 // full runId still present for correlation
    }

    [Fact]
    public void NeedsRevision_AddsRevisionMessage_WithReason()
    {
        var reason = "The build is failing because MainMenu.cs references a missing type.";
        var outcome = new RoundOutcome(RoundKind.NeedsRevision, null, Array.Empty<PriorToolOutput>(),
            "call_rev", new PendingControl(PendingControlKind.Revision, reason),
            "Reviewing the build", null,
            new[] { new RoundToolCall("request_revision", reason, "revision requested") },
            new TokenUsage(), "c3");

        var events = RunEventFactory.BuildRoundEvents("run-1", "task-1", 1, outcome, artifactId: "art-7");

        var msg = Assert.Single(events.Where(e => e.Kind == RunEventKind.RevisionRequested));
        Assert.Equal(reason, msg.Detail);                 // full reason carried for the chat
        Assert.Equal(reason, msg.Title);                  // short enough not to be truncated
        var parent = Assert.Single(events.Where(e => e.Kind == RunEventKind.RoundStarted));
        Assert.Equal(parent.Id, msg.ParentId);            // nested under the round, like AgentMessage
    }
}
