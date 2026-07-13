using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

/// <summary>
/// The board's multiplexed stream: one AgentEvent must reach BOTH its run stream and the stream of the board
/// that run belongs to — and resolving "which board" must cost one Cosmos read per run, not per event.
/// </summary>
public class SseBoardFanoutTests
{
    /// <summary>Counts GetRunAsync calls, so the cache and single-flight can be pinned rather than assumed.</summary>
    private sealed class CountingCosmos(InMemoryCosmosDbService inner) : ICosmosDbService
    {
        public int GetRunCalls;

        public Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetRunCalls);
            return inner.GetRunAsync(taskId, runId, ct);
        }

        // Everything else just forwards.
        public Task EnsureInfrastructureAsync() => inner.EnsureInfrastructureAsync();
        public Task<Board> CreateBoardAsync(Board b, CancellationToken ct = default) => inner.CreateBoardAsync(b, ct);
        public Task<IEnumerable<Board>> GetBoardsAsync(string t, CancellationToken ct = default) => inner.GetBoardsAsync(t, ct);
        public Task<Board?> GetBoardAsync(string t, string b, CancellationToken ct = default) => inner.GetBoardAsync(t, b, ct);
        public Task<Board> UpdateBoardAsync(Board b, CancellationToken ct = default) => inner.UpdateBoardAsync(b, ct);
        public Task DeleteBoardAsync(string t, string b, CancellationToken ct = default) => inner.DeleteBoardAsync(t, b, ct);
        public Task<AgentTask> CreateTaskAsync(AgentTask t, CancellationToken ct = default) => inner.CreateTaskAsync(t, ct);
        public Task<AgentTask?> GetTaskAsync(string b, string t, CancellationToken ct = default) => inner.GetTaskAsync(b, t, ct);
        public Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string b, CancellationToken ct = default) => inner.GetTasksByBoardAsync(b, ct);
        public Task<AgentTask> UpdateTaskAsync(AgentTask t, CancellationToken ct = default) => inner.UpdateTaskAsync(t, ct);
        public Task<AgentTask?> TryClaimTaskForRunAsync(string b, string t, string r, string s, CancellationToken ct = default) => inner.TryClaimTaskForRunAsync(b, t, r, s, ct);
        public Task DeleteTaskAsync(string b, string t, CancellationToken ct = default) => inner.DeleteTaskAsync(b, t, ct);
        public Task PurgeTaskWorkDataAsync(string te, string b, string t, CancellationToken ct = default) => inner.PurgeTaskWorkDataAsync(te, b, t, ct);
        public Task<IEnumerable<Connection>> GetConnectionsAsync(string t, CancellationToken ct = default) => inner.GetConnectionsAsync(t, ct);
        public Task<Connection?> GetConnectionAsync(string t, string c, CancellationToken ct = default) => inner.GetConnectionAsync(t, c, ct);
        public Task<Connection> UpsertConnectionAsync(Connection c, CancellationToken ct = default) => inner.UpsertConnectionAsync(c, ct);
        public Task DeleteConnectionAsync(string t, string c, CancellationToken ct = default) => inner.DeleteConnectionAsync(t, c, ct);
        public Task<ToolPolicy?> GetToolPolicyAsync(string t, CancellationToken ct = default) => inner.GetToolPolicyAsync(t, ct);
        public Task<ToolPolicy> UpsertToolPolicyAsync(ToolPolicy p, CancellationToken ct = default) => inner.UpsertToolPolicyAsync(p, ct);
        public Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string t, CancellationToken ct = default) => inner.GetAgentRolesAsync(t, ct);
        public Task<AgentRole> UpsertAgentRoleAsync(AgentRole r, CancellationToken ct = default) => inner.UpsertAgentRoleAsync(r, ct);
        public Task<AgentRole?> GetAgentRoleAsync(string t, string r, CancellationToken ct = default) => inner.GetAgentRoleAsync(t, r, ct);
        public Task DeleteAgentRoleAsync(string t, string r, CancellationToken ct = default) => inner.DeleteAgentRoleAsync(t, r, ct);
        public Task<WorkflowRun> CreateRunAsync(WorkflowRun r, CancellationToken ct = default) => inner.CreateRunAsync(r, ct);
        public Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string t, CancellationToken ct = default) => inner.GetRunsByTaskAsync(t, ct);
        public Task<WorkflowRun> UpdateRunAsync(WorkflowRun r, CancellationToken ct = default) => inner.UpdateRunAsync(r, ct);
        public Task<Artifact> CreateArtifactAsync(Artifact a, CancellationToken ct = default) => inner.CreateArtifactAsync(a, ct);
        public Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string t, CancellationToken ct = default) => inner.GetArtifactVersionsAsync(t, ct);
        public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction i, CancellationToken ct = default) => inner.CreateInteractionAsync(i, ct);
        public Task<HumanInteraction?> GetInteractionAsync(string r, string i, CancellationToken ct = default) => inner.GetInteractionAsync(r, i, ct);
        public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction i, CancellationToken ct = default) => inner.UpdateInteractionAsync(i, ct);
        public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string t, CancellationToken ct = default) => inner.GetPendingInteractionsAsync(t, ct);
        public Task<TaskEdge> CreateEdgeAsync(TaskEdge e, CancellationToken ct = default) => inner.CreateEdgeAsync(e, ct);
        public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string b, CancellationToken ct = default) => inner.GetEdgesByBoardAsync(b, ct);
        public Task<TaskEdge?> GetEdgeAsync(string b, string e, CancellationToken ct = default) => inner.GetEdgeAsync(b, e, ct);
        public Task<TaskEdge> UpdateEdgeAsync(TaskEdge e, CancellationToken ct = default) => inner.UpdateEdgeAsync(e, ct);
        public Task DeleteEdgeAsync(string b, string e, CancellationToken ct = default) => inner.DeleteEdgeAsync(b, e, ct);
        public Task ResetTaskUsageSessionAsync(string te, string t, string s, CancellationToken ct = default) => inner.ResetTaskUsageSessionAsync(te, t, s, ct);
        public Task<TectikaAgents.Core.Usage.UsageRollup?> GetUsageRollupAsync(string t, string i, CancellationToken ct = default) => inner.GetUsageRollupAsync(t, i, ct);
        public Task<List<TectikaAgents.Core.Usage.UsageRollup>> GetUsageRollupsForTenantAsync(string t, CancellationToken ct = default) => inner.GetUsageRollupsForTenantAsync(t, ct);
        public Task UpsertUsageRollupAsync(TectikaAgents.Core.Usage.UsageRollup r, CancellationToken ct = default) => inner.UpsertUsageRollupAsync(r, ct);
        public Task UpsertUsageEventAsync(TectikaAgents.Core.Usage.UsageEvent e, CancellationToken ct = default) => inner.UpsertUsageEventAsync(e, ct);
        public Task<TectikaAgents.Core.Usage.UsageEventsPage> GetUsageEventsForTaskAsync(string te, string t, int m, string? c, CancellationToken ct = default) => inner.GetUsageEventsForTaskAsync(te, t, m, c, ct);
        public Task<List<TectikaAgents.Core.Usage.UsageTimePoint>> GetUsageTimeSeriesAsync(string s, string i, int d, CancellationToken ct = default) => inner.GetUsageTimeSeriesAsync(s, i, d, ct);
        public Task<List<TectikaAgents.Core.Usage.AgentUsage>> GetUsageByAgentAsync(string s, string i, int d, CancellationToken ct = default) => inner.GetUsageByAgentAsync(s, i, d, ct);
        public Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string t, int? s = null, CancellationToken ct = default) => inner.GetRunEventsAsync(t, s, ct);
        public Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default) => inner.CreateRunEventAsync(e, ct);
        public Task<RunEvent> UpdateRunEventAsync(RunEvent e, CancellationToken ct = default) => inner.UpdateRunEventAsync(e, ct);
        public Task DeleteEdgesForTaskAsync(string b, string t, CancellationToken ct = default) => inner.DeleteEdgesForTaskAsync(b, t, ct);
        public Task<PreviewSession?> GetPreviewAsync(string b, CancellationToken ct = default) => inner.GetPreviewAsync(b, ct);
        public Task UpsertPreviewAsync(PreviewSession s, CancellationToken ct = default) => inner.UpsertPreviewAsync(s, ct);
        public Task DeletePreviewAsync(string b, string i, CancellationToken ct = default) => inner.DeletePreviewAsync(b, i, ct);
        public Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default) => inner.ListActivePreviewsAsync(ct);
        public Task<TaskComment> CreateCommentAsync(TaskComment c, CancellationToken ct = default) => inner.CreateCommentAsync(c, ct);
        public Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string t, CancellationToken ct = default) => inner.GetCommentsByTaskAsync(t, ct);
        public Task<TaskComment?> GetCommentAsync(string t, string c, CancellationToken ct = default) => inner.GetCommentAsync(t, c, ct);
        public Task<TaskComment> UpsertCommentAsync(TaskComment c, CancellationToken ct = default) => inner.UpsertCommentAsync(c, ct);
        public Task<Channel> CreateChannelAsync(Channel c, CancellationToken ct = default) => inner.CreateChannelAsync(c, ct);
        public Task<IReadOnlyList<Channel>> GetChannelsByTenantAsync(string t, CancellationToken ct = default) => inner.GetChannelsByTenantAsync(t, ct);
        public Task<Channel?> GetChannelAsync(string t, string c, CancellationToken ct = default) => inner.GetChannelAsync(t, c, ct);
        public Task<Channel> UpsertChannelAsync(Channel c, CancellationToken ct = default) => inner.UpsertChannelAsync(c, ct);
        public Task<IReadOnlyList<Channel>> GetChannelsForBoardAsync(string t, string b, CancellationToken ct = default) => inner.GetChannelsForBoardAsync(t, b, ct);
        public Task<ChannelMessage> CreateChannelMessageAsync(ChannelMessage m, CancellationToken ct = default) => inner.CreateChannelMessageAsync(m, ct);
        public Task<IReadOnlyList<ChannelMessage>> GetChannelMessagesAsync(string c, DateTimeOffset? s = null, CancellationToken ct = default) => inner.GetChannelMessagesAsync(c, s, ct);
        public Task<ChannelMessage?> GetChannelMessageAsync(string c, string m, CancellationToken ct = default) => inner.GetChannelMessageAsync(c, m, ct);
        public Task<ChannelMessage> UpsertChannelMessageAsync(ChannelMessage m, CancellationToken ct = default) => inner.UpsertChannelMessageAsync(m, ct);
    }

    /// <summary>A hub + manager pair over a cosmos that has one run ("run-1" on task "t1", board "b1").</summary>
    private static async Task<(SseHub hub, SseConnectionManager sse, CountingCosmos cosmos)> SetupAsync(bool seedIndex)
    {
        var inner = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        var cosmos = new CountingCosmos(inner);
        await cosmos.CreateRunAsync(new WorkflowRun { Id = "run-1", TaskId = "t1", BoardId = "b1", TenantId = "default" });

        var hub = new SseHub(NullLogger<SseHub>.Instance);
        // seedIndex=false simulates a cold cache (a restarted API, or a run created by another replica), which
        // is exactly the path the lazy Cosmos fallback exists for.
        var index = new RunBoardIndex();
        if (seedIndex) index.Remember("run-1", "t1", "b1");
        var sse = new SseConnectionManager(hub, new RunBoardResolver(index, cosmos, NullLogger<RunBoardResolver>.Instance),
            NullLogger<SseConnectionManager>.Instance);
        return (hub, sse, cosmos);
    }

    private static (SseClient client, StringWriter sink) Subscribe(SseHub hub, string key)
    {
        var sink = new StringWriter();
        var client = new SseClient(sink, CancellationToken.None);
        hub.Add(key, client);
        return (client, sink);
    }

    [Fact]
    public async Task Event_reaches_both_its_run_stream_and_its_board_stream()
    {
        var (hub, sse, _) = await SetupAsync(seedIndex: true);
        var (_, runSink) = Subscribe(hub, SseKeys.Run("run-1"));
        var (_, boardSink) = Subscribe(hub, SseKeys.Board("b1"));

        await sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = "t1" });

        Assert.Contains("\"runId\":\"run-1\"", runSink.ToString());
        Assert.Contains("\"runId\":\"run-1\"", boardSink.ToString());
        Assert.EndsWith("\n\n", runSink.ToString());
    }

    [Fact]
    public async Task Event_does_not_leak_to_another_board()
    {
        var (hub, sse, _) = await SetupAsync(seedIndex: true);
        var (_, otherSink) = Subscribe(hub, SseKeys.Board("b2"));

        await sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = "t1" });

        Assert.Equal("", otherSink.ToString());
    }

    [Fact]
    public async Task Client_subscribed_to_both_keys_receives_one_copy()
    {
        var (hub, sse, _) = await SetupAsync(seedIndex: true);
        var sink = new StringWriter();
        var client = new SseClient(sink, CancellationToken.None);
        hub.Add(SseKeys.Run("run-1"), client);
        hub.Add(SseKeys.Board("b1"), client);

        await sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = "t1" });

        var frames = sink.ToString().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(frames);
    }

    [Fact]
    public async Task Cold_cache_resolves_the_board_with_exactly_one_cosmos_read_for_many_events()
    {
        var (hub, sse, cosmos) = await SetupAsync(seedIndex: false);
        var (_, boardSink) = Subscribe(hub, SseKeys.Board("b1"));

        for (var i = 0; i < 50; i++)
            await sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = "t1" });

        Assert.Equal(50, boardSink.ToString().Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(1, cosmos.GetRunCalls);   // the other 49 came from the cache
    }

    [Fact]
    public async Task Concurrent_events_for_a_new_run_share_one_lookup()
    {
        var (hub, sse, cosmos) = await SetupAsync(seedIndex: false);
        Subscribe(hub, SseKeys.Board("b1"));

        // The Service Bus listener processes up to 10 messages at once; without single-flight these would be
        // 10 simultaneous point reads for the same document.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = "t1" })));

        Assert.Equal(1, cosmos.GetRunCalls);
    }

    [Fact]
    public async Task Event_that_beats_its_run_document_to_cosmos_is_not_black_holed()
    {
        // An event can be broadcast before its WorkflowRun is persisted. Persisting it must un-blacklist the
        // run immediately — CreateRunAsync is the choke-point every run-creation path goes through, so seeding
        // the index there is what keeps a live run from being muted for the whole negative TTL.
        var index = new RunBoardIndex();
        var inner = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance, index);
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var sse = new SseConnectionManager(hub, new RunBoardResolver(index, inner, NullLogger<RunBoardResolver>.Instance),
            NullLogger<SseConnectionManager>.Instance);
        var (_, boardSink) = Subscribe(hub, SseKeys.Board("b1"));

        var ev = new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-late", TaskId = "t1" };
        await sse.BroadcastAsync(ev);                       // run doc doesn't exist yet → board misses it
        Assert.Equal("", boardSink.ToString());

        await inner.CreateRunAsync(new WorkflowRun { Id = "run-late", TaskId = "t1", BoardId = "b1", TenantId = "default" });
        await sse.BroadcastAsync(ev);

        Assert.Contains("run-late", boardSink.ToString());
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Negative_cache_expires_so_a_run_written_by_another_replica_is_picked_up()
    {
        // The other half of the black-hole guard: when the run is created somewhere this process's index can't
        // see (another replica, or before a restart), only the TTL gets us out. A permanent negative would mean
        // the board never shows that run's events again.
        var clock = new FakeClock();
        var index = new RunBoardIndex(clock);
        var inner = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);   // no index → no seeding
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var sse = new SseConnectionManager(hub, new RunBoardResolver(index, inner, NullLogger<RunBoardResolver>.Instance),
            NullLogger<SseConnectionManager>.Instance);
        var (_, boardSink) = Subscribe(hub, SseKeys.Board("b1"));

        var ev = new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-late", TaskId = "t1" };
        await sse.BroadcastAsync(ev);
        Assert.Equal("", boardSink.ToString());

        await inner.CreateRunAsync(new WorkflowRun { Id = "run-late", TaskId = "t1", BoardId = "b1", TenantId = "default" });

        await sse.BroadcastAsync(ev);                       // still inside the TTL — negative cache holds
        Assert.Equal("", boardSink.ToString());

        clock.Now = clock.Now.AddMinutes(1);               // TTL is 45s
        await sse.BroadcastAsync(ev);
        Assert.Contains("run-late", boardSink.ToString());
    }

    [Fact]
    public async Task Event_without_a_taskId_goes_to_the_run_stream_only()
    {
        var (hub, sse, cosmos) = await SetupAsync(seedIndex: false);
        var (_, runSink) = Subscribe(hub, SseKeys.Run("run-1"));
        var (_, boardSink) = Subscribe(hub, SseKeys.Board("b1"));

        // Runs are partitioned by taskId, so without one there is no point read to make — and no throw.
        await sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = null });

        Assert.Contains("run-1", runSink.ToString());
        Assert.Equal("", boardSink.ToString());
        Assert.Equal(0, cosmos.GetRunCalls);
    }

    [Fact]
    public async Task Add_and_remove_while_broadcasting_does_not_throw()
    {
        // The old manager mutated a bare List<SseClient> from add/remove/broadcast concurrently.
        var (hub, sse, _) = await SetupAsync(seedIndex: true);
        var churn = Task.Run(() => Parallel.For(0, 200, i =>
        {
            var client = new SseClient(new StringWriter(), CancellationToken.None);
            hub.Add(SseKeys.Board("b1"), client);
            hub.Remove(SseKeys.Board("b1"), client);
        }));

        for (var i = 0; i < 200; i++)
            await sse.BroadcastAsync(new AgentEvent { Type = AgentEvent.Types.RunEvent, RunId = "run-1", TaskId = "t1" });

        await churn;   // no exception is the assertion
    }

    [Fact]
    public async Task Concurrent_writers_to_one_client_produce_whole_frames()
    {
        // The heartbeat loop and a broadcast write the same StreamWriter. Without SseClient's gate the frames
        // interleave and the browser sees corrupt SSE.
        var sink = new StringWriter();
        var client = new SseClient(sink, CancellationToken.None);

        await Task.WhenAll(
            Task.Run(async () => { for (var i = 0; i < 500; i++) await client.WriteAsync("data: aaaaaaaaaa\n\n"); }),
            Task.Run(async () => { for (var i = 0; i < 500; i++) await client.WriteAsync(": ping\n\n"); }));

        var frames = sink.ToString().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1000, frames.Length);
        Assert.All(frames, f => Assert.True(f is "data: aaaaaaaaaa" or ": ping", $"torn frame: {f}"));
    }
}
