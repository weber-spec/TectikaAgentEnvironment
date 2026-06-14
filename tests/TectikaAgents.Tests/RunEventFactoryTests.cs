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
}
