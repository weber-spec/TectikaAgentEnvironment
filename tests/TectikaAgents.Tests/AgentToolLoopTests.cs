using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentToolLoopTests
{
    // Minimal fake explorer: get_board_overview returns one task; get_artifact returns content.
    private sealed class FakeExplorer : IProjectExplorer
    {
        public int OverviewCalls;
        public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
        { OverviewCalls++; return Task.FromResult(new BoardOverview("b","Board",
            new[]{ new TaskNode("u1","Upstream","Done","agent-x", Array.Empty<string>()) })); }
        public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string q, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<TaskSummary>)Array.Empty<TaskSummary>());
        public Task<TaskDetail?> GetTaskAsync(string id, CancellationToken ct = default)
            => Task.FromResult<TaskDetail?>(null);
        public Task<ArtifactView?> GetArtifactAsync(string id, int? v, CancellationToken ct = default)
            => Task.FromResult<ArtifactView?>(new ArtifactView(id, 1, "Markdown", "UPSTREAM-CONTENT"));
    }

    private static ToolCall FC(string name, object args) =>
        new(name, JsonSerializer.Serialize(args), $"call_{name}");

    [Fact]
    public async Task Loop_ExecutesExploreTool_ThenReturnsFinalText()
    {
        var explorer = new FakeExplorer();
        var rounds = new Queue<RoundResponse>(new[]
        {
            // round 1: ask for the board overview
            RoundResponse.Tools(new[]{ FC("get_board_overview", new {}) }),
            // round 2: produce final answer (loop ends)
            RoundResponse.Final("# Done\n\nUsed the board.", new TokenUsage{ Input=10, Output=5 }),
        });

        var loop = new AgentToolLoop(explorer);
        var toolEvents = new List<string>();
        var result = await loop.RunAsync(
            sendRound: (inputs, ct) => Task.FromResult(rounds.Dequeue()),
            maxRounds: 8,
            onToolCall: (name, args) => toolEvents.Add(name),
            ct: default);

        Assert.Equal(1, explorer.OverviewCalls);
        Assert.Contains("get_board_overview", toolEvents);
        Assert.Equal("# Done\n\nUsed the board.", result.FinalText);
        Assert.Null(result.Control);
    }

    [Fact]
    public async Task Loop_CapturesControlTools_AndStops()
    {
        var rounds = new Queue<RoundResponse>(new[]
        {
            RoundResponse.Tools(new[]{ FC("round_intent", new { text = "Checking budget" }),
                                       FC("request_human_input", new { question = "Which hotel?", options = new[]{"A","B"} }) }),
        });
        var loop = new AgentToolLoop(new FakeExplorer());
        var result = await loop.RunAsync((i,c)=>Task.FromResult(rounds.Dequeue()), 8, (_,__)=>{}, default);

        Assert.Equal("Checking budget", result.RoundIntent);
        Assert.NotNull(result.Control);
        Assert.Equal(PendingControlKind.HumanInput, result.Control!.Kind);
        Assert.Equal("Which hotel?", result.Control.Text);
        Assert.Equal(new[]{"A","B"}, result.Control.Options);
    }

    [Fact]
    public async Task Loop_StopsAtMaxRounds()
    {
        // Always returns a tool call → never finishes on its own.
        var loop = new AgentToolLoop(new FakeExplorer());
        var result = await loop.RunAsync(
            (i,c)=>Task.FromResult(RoundResponse.Tools(new[]{ FC("get_board_overview", new {}) })),
            maxRounds: 3, onToolCall:(_,__)=>{}, ct: default);
        Assert.True(result.MaxRoundsHit);
        Assert.Equal(3, result.Rounds);
    }
}
