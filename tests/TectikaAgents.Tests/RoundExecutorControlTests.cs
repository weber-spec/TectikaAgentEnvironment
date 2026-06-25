using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

/// <summary>A round can pause for a human only once. The FIRST control tool becomes the open call answered
/// on resume; any additional control tool in the same round must be closed immediately so it can't dangle
/// and poison the task's reused Foundry conversation on the next run ("No tool output found for …").</summary>
public class RoundExecutorControlTests
{
    [Fact]
    public async Task TwoControlCalls_FirstAwaitsHuman_SecondClosedNotDangling()
    {
        var round = RoundResponse.Tools(new[]
        {
            new ToolCall("request_human_input", """{"question":"Which region?"}""", "call_ask"),
            new ToolCall("request_approval", """{"description":"Delete prod DB?"}""", "call_approve"),
        });

        var p = await RoundExecutor.ExecuteOneRoundAsync(
            round, new NeverExplorer(), (_, __) => { },
            gitHub: null, boardRepo: null, role: null,
            workspace: null, workspaceProvider: null, ct: default);

        // The first control tool is the pause we await a human reply on.
        Assert.Equal("call_ask", p.OpenControlCallId);
        Assert.NotNull(p.Control);
        Assert.Equal(PendingControlKind.HumanInput, p.Control!.Kind);

        // The extra control tool is answered NOW (so it can't dangle) and is not the open call.
        var extra = Assert.Single(p.ToolOutputs);
        Assert.Equal("call_approve", extra.CallId);
        Assert.NotEqual(p.OpenControlCallId, extra.CallId);
        Assert.Contains("not_taken", extra.Output);
    }

    [Fact]
    public async Task SingleControlCall_LeftOpenWithNoOutput()
    {
        var round = RoundResponse.Tools(new[]
        {
            new ToolCall("request_human_input", """{"question":"Proceed?"}""", "call_only"),
        });

        var p = await RoundExecutor.ExecuteOneRoundAsync(
            round, new NeverExplorer(), (_, __) => { },
            gitHub: null, boardRepo: null, role: null,
            workspace: null, workspaceProvider: null, ct: default);

        Assert.Equal("call_only", p.OpenControlCallId);
        Assert.Empty(p.ToolOutputs);   // the sole control call is answered later by the human's reply
    }

    // Control tools never touch the explorer; these throw to prove that.
    private sealed class NeverExplorer : IProjectExplorer
    {
        public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string q, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail?> GetTaskAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ArtifactView?> GetArtifactAsync(string id, int? v, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
