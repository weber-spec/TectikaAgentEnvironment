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
        public Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SharedNote>)Array.Empty<SharedNote>());
    }

    /// <summary>Explorer whose get_artifact returns a configurable body or throws — for the cap/scrub/error paths.</summary>
    private sealed class ConfigurableExplorer : IProjectExplorer
    {
        private readonly Func<ArtifactView?> _artifact;
        public ConfigurableExplorer(Func<ArtifactView?> artifact) => _artifact = artifact;
        public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
            => Task.FromResult(new BoardOverview("b", "Board", Array.Empty<TaskNode>()));
        public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string q, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<TaskSummary>)Array.Empty<TaskSummary>());
        public Task<TaskDetail?> GetTaskAsync(string id, CancellationToken ct = default)
            => Task.FromResult<TaskDetail?>(null);
        public Task<ArtifactView?> GetArtifactAsync(string id, int? v, CancellationToken ct = default)
            => Task.FromResult(_artifact());   // may throw
        public Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SharedNote>)Array.Empty<SharedNote>());
    }

    private static ToolCall FC(string name, object args) => new(name, JsonSerializer.Serialize(args), $"call_{name}");

    [Fact]
    public async Task ToolThatThrows_BecomesErrorOutput_RoundNotAborted()
    {
        var explorer = new ConfigurableExplorer(() => throw new InvalidOperationException("cosmos 429"));
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[]
            {
                FC("get_artifact", new { taskId = "t1" }),     // throws
                FC("get_board_overview", new { }),             // still runs
            }),
            explorer, (_, __) => { }, null, null, null, null, null, default);

        Assert.False(r.IsFinal);
        Assert.Equal(2, r.ToolOutputs.Count);                  // both calls answered
        var errOut = r.ToolOutputs.Single(o => o.CallId == "call_get_artifact");
        Assert.Contains("error", errOut.Output);
        Assert.Contains("cosmos 429", errOut.Output);
        Assert.Contains(r.ToolOutputs, o => o.CallId == "call_get_board_overview");
    }

    [Fact]
    public async Task MalformedArgsJson_BecomesErrorOutput()
    {
        var bad = new ToolCall("get_task", "{ not json", "call_bad");
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[] { bad }), new FakeExplorer(), (_, __) => { }, null, null, null, null, null, default);
        Assert.Single(r.ToolOutputs);
        Assert.Equal("call_bad", r.ToolOutputs[0].CallId);
        Assert.Contains("error", r.ToolOutputs[0].Output);
    }

    [Fact]
    public async Task OversizedOutput_IsTruncated()
    {
        var huge = new string('x', 100_000);
        var explorer = new ConfigurableExplorer(() => new ArtifactView("t1", 1, "Markdown", huge));
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[] { FC("get_artifact", new { taskId = "t1" }) }),
            explorer, (_, __) => { }, null, null, null, null, null, default);
        Assert.True(r.ToolOutputs[0].Output.Length < 60_000);
        Assert.Contains("truncated", r.ToolOutputs[0].Output);
    }

    [Fact]
    public async Task SecretShapedContent_IsScrubbed_FromOutput()
    {
        const string token = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
        var explorer = new ConfigurableExplorer(() => new ArtifactView("t1", 1, "Markdown", $"leaked {token}"));
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[] { FC("get_artifact", new { taskId = "t1" }) }),
            explorer, (_, __) => { }, null, null, null, null, null, default);
        Assert.DoesNotContain(token, r.ToolOutputs[0].Output);
    }

    [Fact]
    public async Task NoToolCalls_IsFinal()
    {
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Final("DONE", new TokenUsage()), new FakeExplorer(), (_, __) => { }, null, null, null, null, null, default);
        Assert.True(r.IsFinal);
        Assert.Equal("DONE", r.FinalText);
        Assert.Empty(r.ToolOutputs);
    }

    [Fact]
    public async Task ExploreTool_ProducesOutput_NotFinal()
    {
        var r = await RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(new[] { FC("get_board_overview", new { }) }), new FakeExplorer(), (_, __) => { }, null, null, null, null, null, default);
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
            new FakeExplorer(), (_, __) => { }, null, null, null, null, null, default);

        Assert.False(r.IsFinal);
        Assert.Equal("Plan", r.RoundIntent);
        Assert.NotNull(r.Control);
        Assert.Equal(PendingControlKind.HumanInput, r.Control!.Kind);
        Assert.Equal("call_request_human_input", r.OpenControlCallId);
        // explore + the round_intent ack are still submitted alongside the control on resume
        Assert.Contains(r.ToolOutputs, o => o.CallId == "call_get_board_overview");
    }

    [Fact]
    public async Task Routes_mcp_tool_call_to_executor()
    {
        var secrets = new FakeSecretProvider();
        secrets.Store["s1"] = "xoxb-abc";
        var slack = new FakeFirstPartyConnector { CatalogId = "slack", Result = "{\"channels\":[]}" };
        var mcp = new TectikaAgents.AgentRuntime.Mcp.McpToolExecutor(new FakeMcpGateway(), secrets, new[] { slack });
        var conns = new System.Collections.Generic.List<TectikaAgents.Core.Models.Connection>
        {
            new() { Id = "c-slack", CatalogId = "slack", SecretName = "s1", Status = TectikaAgents.Core.Models.ConnectionStatus.Connected }
        };
        var role = new TectikaAgents.Core.Models.AgentRole
        { Connections = { new TectikaAgents.Core.Models.AgentConnectionRef { ConnectionId = "c-slack", CatalogId = "slack" } } };
        var resp = RoundResponse.Tools(new[] { new ToolCall("slack__list_channels", "{}", "call-1") });

        var result = await RoundExecutor.ExecuteOneRoundAsync(
            resp, new NullProjectExplorer(), (_, _) => { },
            gitHub: null, boardRepo: null, role: role,
            workspace: null, workspaceProvider: null,
            ct: System.Threading.CancellationToken.None,
            mcp: mcp, connections: conns);

        Assert.Single(result.ToolOutputs);
        Assert.Contains("channels", result.ToolOutputs[0].Output);
        Assert.Equal("list_channels", slack.LastTool);
    }
}
