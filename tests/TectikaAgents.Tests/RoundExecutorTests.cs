using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

public class RoundExecutorTests
{
    private sealed class FakeExplorer : IProjectExplorer
    {
        public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
            => Task.FromResult(new BoardOverview("b", "Board",
                new[] { new TaskNode("u1", "Up", "Done", "agent-x", Array.Empty<string>()) }));
        public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string q, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<TaskSummary>)Array.Empty<TaskSummary>());
        public Task<TaskDetail?> GetTaskAsync(string id, CancellationToken ct = default)
            => Task.FromResult<TaskDetail?>(null);
        public Task<ArtifactView?> GetArtifactAsync(string id, int? v, CancellationToken ct = default)
            => Task.FromResult<ArtifactView?>(new ArtifactView(id, 1, "Markdown", "C"));
    }

    private static ToolCall FC(string name, object args) => new(name, JsonSerializer.Serialize(args), $"call_{name}");

    [Fact]
    public async Task NoToolCalls_IsFinal()
    {
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Final("DONE", new TokenUsage()), new FakeExplorer(), (_, __) => { }, default);
        Assert.True(r.IsFinal);
        Assert.Equal("DONE", r.FinalText);
        Assert.Empty(r.ToolOutputs);
    }

    [Fact]
    public async Task ExploreTool_ProducesOutput_NotFinal()
    {
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[] { FC("get_board_overview", new { }) }), new FakeExplorer(), (_, __) => { }, default);
        Assert.False(r.IsFinal);
        Assert.Null(r.Control);
        Assert.Single(r.ToolOutputs);
        Assert.Equal("call_get_board_overview", r.ToolOutputs[0].CallId);
        Assert.Contains("get_board_overview", r.ToolCalls.Select(t => t.Name));
    }

    [Fact]
    public async Task ControlTool_CapturesControl_AndOpenCallId_KeepsExploreOutputs()
    {
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[]
            {
                FC("round_intent", new { text = "Plan" }),
                FC("get_board_overview", new { }),
                FC("request_human_input", new { question = "Which?", options = new[] { "A", "B" } }),
            }),
            new FakeExplorer(), (_, __) => { }, default);

        Assert.False(r.IsFinal);
        Assert.Equal("Plan", r.RoundIntent);
        Assert.NotNull(r.Control);
        Assert.Equal(PendingControlKind.HumanInput, r.Control!.Kind);
        Assert.Equal("call_request_human_input", r.OpenControlCallId);
        // explore + the round_intent ack are still submitted alongside the control on resume
        Assert.Contains(r.ToolOutputs, o => o.CallId == "call_get_board_overview");
    }
}
