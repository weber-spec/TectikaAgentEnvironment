using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Scheduling;
using Xunit;

// TaskStartGate is the one rule that decides whether a task may be started automatically. Three
// callers share it — the Durable cascade, the in-process cascade, and Run Board's server-side
// re-validation — so these tests pin behaviour that used to live in (and drift between) each of them.
public class TaskStartGateTests
{
    private const string Board = "b1";

    private static InMemoryCosmosDbService Cosmos() =>
        new(NullLogger<InMemoryCosmosDbService>.Instance);

    private static async Task<AgentTask> AddTaskAsync(
        InMemoryCosmosDbService cosmos, string id, AgentTaskStatus status,
        AssigneeType assigneeType = AssigneeType.Agent, string agentId = "agent-x")
    {
        return await cosmos.CreateTaskAsync(new AgentTask
        {
            Id = id, BoardId = Board, TenantId = "t", Title = id,
            Status = status,
            Assignee = new TaskAssignee { Type = assigneeType, Id = agentId },
        });
    }

    private static async Task AddEdgeAsync(
        InMemoryCosmosDbService cosmos, string source, string target, EdgeKind kind = EdgeKind.Dependency)
    {
        await cosmos.CreateEdgeAsync(new TaskEdge
        {
            Id = TaskEdge.MakeId(source, target), BoardId = Board, TenantId = "t",
            SourceTaskId = source, TargetTaskId = target, Kind = kind,
        });
    }

    [Fact]
    public async Task Root_with_no_dependencies_can_start()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "root", AgentTaskStatus.Backlog);

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "root");

        Assert.True(decision.CanStart);
        Assert.Equal(TaskStartBlock.None, decision.Block);
    }

    // The bug this gate was built for: the cascade only fires on a parent's *transition* to Done, so a
    // dependency drawn after the parent already finished left the child unstartable forever — the board
    // run skipped it for having an edge, and no cascade would ever fire again.
    [Fact]
    public async Task Dependency_added_after_the_parent_already_finished_can_start()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "parent", AgentTaskStatus.Done);
        await AddTaskAsync(cosmos, "child", AgentTaskStatus.Backlog);
        await AddEdgeAsync(cosmos, "parent", "child");   // drawn *after* the parent reached Done

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "child");

        Assert.True(decision.CanStart);
    }

    [Fact]
    public async Task Unfinished_parent_blocks_and_names_the_blocker()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "done-parent", AgentTaskStatus.Done);
        await AddTaskAsync(cosmos, "busy-parent", AgentTaskStatus.InProgress);
        await AddTaskAsync(cosmos, "child", AgentTaskStatus.Backlog);
        await AddEdgeAsync(cosmos, "done-parent", "child");
        await AddEdgeAsync(cosmos, "busy-parent", "child");

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "child");

        Assert.False(decision.CanStart);
        Assert.Equal(TaskStartBlock.UpstreamNotDone, decision.Block);
        Assert.Equal("busy-parent", decision.BlockingUpstreamId);
        Assert.False(decision.UpstreamMissing);
    }

    // A dangling edge (the parent's document is gone) is a data-integrity problem, not a normal wait —
    // we can't prove its work happened, so we refuse, and flag it separately so it can be diagnosed.
    [Fact]
    public async Task Missing_parent_blocks_and_is_flagged_as_missing()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "child", AgentTaskStatus.Backlog);
        await AddEdgeAsync(cosmos, "ghost", "child");

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "child");

        Assert.False(decision.CanStart);
        Assert.Equal(TaskStartBlock.UpstreamNotDone, decision.Block);
        Assert.Equal("ghost", decision.BlockingUpstreamId);
        Assert.True(decision.UpstreamMissing);
    }

    // QaFeedback is the loop-back arc of a QA cycle, not a prerequisite. If it counted as a dependency,
    // every validated task would deadlock against its own validator.
    [Fact]
    public async Task QaFeedback_edge_is_not_a_dependency()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "validator", AgentTaskStatus.InProgress);
        await AddTaskAsync(cosmos, "impl", AgentTaskStatus.Backlog);
        await AddEdgeAsync(cosmos, "validator", "impl", EdgeKind.QaFeedback);

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "impl");

        Assert.True(decision.CanStart);
    }

    // Failed is NOT startable: the client resets it to Backlog (and clears the conversation) first.
    [Theory]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.InProgress)]
    [InlineData(AgentTaskStatus.Done)]
    [InlineData(AgentTaskStatus.Review)]
    [InlineData(AgentTaskStatus.Blocked)]
    public async Task Non_backlog_task_cannot_start(AgentTaskStatus status)
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "t", status);

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "t");

        Assert.False(decision.CanStart);
        Assert.Equal(TaskStartBlock.NotBacklog, decision.Block);
    }

    [Fact]
    public async Task Human_owned_task_cannot_start()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "t", AgentTaskStatus.Backlog, AssigneeType.Human, "someone@x.com");

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "t");

        Assert.False(decision.CanStart);
        Assert.Equal(TaskStartBlock.NoAgentAssignee, decision.Block);
    }

    // Agent type with an empty role id can't actually run — RunStartService already refused these;
    // the gate now refuses them up front instead of letting a cascade "start" them into a no-op.
    [Fact]
    public async Task Agent_assignee_without_a_role_id_cannot_start()
    {
        var cosmos = Cosmos();
        await AddTaskAsync(cosmos, "t", AgentTaskStatus.Backlog, AssigneeType.Agent, agentId: "");

        var decision = await TaskStartGate.EvaluateAsync(cosmos, Board, "t");

        Assert.False(decision.CanStart);
        Assert.Equal(TaskStartBlock.NoAgentAssignee, decision.Block);
    }

    [Fact]
    public async Task Unknown_task_is_not_found()
    {
        var decision = await TaskStartGate.EvaluateAsync(Cosmos(), Board, "nope");

        Assert.False(decision.CanStart);
        Assert.Equal(TaskStartBlock.NotFound, decision.Block);
    }
}
