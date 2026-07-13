using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using Xunit;

// RunStartService's optional dependency gate. Run Board sets respectDependencies because it fans out
// over a board snapshot that can be a poll interval stale; a manual per-task run leaves it off and keeps
// its ability to force a run over unmet dependencies.
public class RunStartDependencyGateTests
{
    private const string Board = "b1";

    // Durable's /steerable/start always succeeds here — these tests are about what happens *before* it.
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"instanceId\":\"instance-1\"}", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static RunStartService Service(InMemoryCosmosDbService cosmos) =>
        new(cosmos,
            new StubHttpClientFactory(new OkHandler()),
            Options.Create(new DurableFunctionsSettings { StartUrl = "http://localhost:7071/api/pipelines/start" }),
            NullLogger<RunStartService>.Instance);

    private static async Task<InMemoryCosmosDbService> BoardWithParentAndChild(AgentTaskStatus parentStatus)
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        foreach (var (id, status) in new[] { ("parent", parentStatus), ("child", AgentTaskStatus.Backlog) })
        {
            await cosmos.CreateTaskAsync(new AgentTask
            {
                Id = id, BoardId = Board, TenantId = "t", Title = id, Status = status,
                Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = "agent-x" },
            });
        }
        await cosmos.CreateEdgeAsync(new TaskEdge
        {
            Id = TaskEdge.MakeId("parent", "child"), BoardId = Board, TenantId = "t",
            SourceTaskId = "parent", TargetTaskId = "child", Kind = EdgeKind.Dependency,
        });
        return cosmos;
    }

    // The important assertion is the second one: the gate runs BEFORE the atomic claim, so a blocked
    // task is left untouched in Backlog rather than flipped to InProgress and stranded there.
    [Fact]
    public async Task Blocks_and_leaves_the_task_in_backlog_when_a_parent_is_unfinished()
    {
        var cosmos = await BoardWithParentAndChild(AgentTaskStatus.InProgress);

        var run = await Service(cosmos).StartAsync(Board, "child", "t", respectDependencies: true);

        Assert.Null(run);
        var child = await cosmos.GetTaskAsync(Board, "child");
        Assert.Equal(AgentTaskStatus.Backlog, child!.Status);
        Assert.Null(child.WorkflowRunId);
    }

    [Fact]
    public async Task Starts_when_every_parent_is_done()
    {
        var cosmos = await BoardWithParentAndChild(AgentTaskStatus.Done);

        var run = await Service(cosmos).StartAsync(Board, "child", "t", respectDependencies: true);

        Assert.NotNull(run);
        var child = await cosmos.GetTaskAsync(Board, "child");
        Assert.Equal(AgentTaskStatus.InProgress, child!.Status);
    }

    // Without the flag the gate is skipped entirely — this is the per-task "Run" button, which is
    // deliberately allowed to jump ahead of unfinished upstream work.
    [Fact]
    public async Task Manual_run_still_forces_past_an_unfinished_parent()
    {
        var cosmos = await BoardWithParentAndChild(AgentTaskStatus.InProgress);

        var run = await Service(cosmos).StartAsync(Board, "child", "t");

        Assert.NotNull(run);
        var child = await cosmos.GetTaskAsync(Board, "child");
        Assert.Equal(AgentTaskStatus.InProgress, child!.Status);
    }

    [Fact]
    public async Task Root_with_no_dependencies_starts_under_the_gate()
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        await cosmos.CreateTaskAsync(new AgentTask
        {
            Id = "root", BoardId = Board, TenantId = "t", Title = "root", Status = AgentTaskStatus.Backlog,
            Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = "agent-x" },
        });

        var run = await Service(cosmos).StartAsync(Board, "root", "t", respectDependencies: true);

        Assert.NotNull(run);
    }
}
