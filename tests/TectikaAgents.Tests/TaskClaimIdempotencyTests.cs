using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

public class TaskClaimIdempotencyTests
{
    private static async Task<InMemoryCosmosDbService> WithBacklogTask(string boardId, string taskId)
    {
        var svc = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        await svc.CreateTaskAsync(new AgentTask
        {
            Id = taskId, BoardId = boardId, TenantId = "t", Title = "T",
            Status = AgentTaskStatus.Backlog,
            Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = "agent-x" },
        });
        return svc;
    }

    [Fact]
    public async Task Concurrent_claims_only_one_wins()
    {
        var svc = await WithBacklogTask("b1", "task-1");

        var attempts = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => svc.TryClaimTaskForRunAsync("b1", "task-1", $"run-{i}", "sess")))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r is not null));   // exactly one winner

        var task = await svc.GetTaskAsync("b1", "task-1");
        Assert.Equal(AgentTaskStatus.InProgress, task!.Status);
        Assert.StartsWith("run-", task.WorkflowRunId);          // linked to the winning run
    }

    [Fact]
    public async Task Claim_fails_when_not_backlog()
    {
        var svc = await WithBacklogTask("b1", "task-2");
        Assert.NotNull(await svc.TryClaimTaskForRunAsync("b1", "task-2", "run-a", "sess"));   // first wins
        Assert.Null(await svc.TryClaimTaskForRunAsync("b1", "task-2", "run-b", "sess"));      // second blocked
    }

    [Fact]
    public async Task Claim_null_for_missing_or_wrong_board()
    {
        var svc = await WithBacklogTask("b1", "task-3");
        Assert.Null(await svc.TryClaimTaskForRunAsync("b1", "nope", "run-a", "sess"));   // missing
        Assert.Null(await svc.TryClaimTaskForRunAsync("other", "task-3", "run-a", "sess"));   // wrong board
    }
}
