using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class UsageRollupTests
{
    [Fact]
    public void Add_accumulates_tokens_cost_and_count()
    {
        var b = new UsageBucket();
        b.Add(new TokenUsage { Input = 100, CachedInput = 10, Output = 50, Reasoning = 5 }, 0.25m);
        b.Add(new TokenUsage { Input = 200, Output = 100 }, 0.50m);
        Assert.Equal(300, b.Tokens.Input);
        Assert.Equal(10, b.Tokens.CachedInput);
        Assert.Equal(150, b.Tokens.Output);
        Assert.Equal(0.75m, b.CostUsd);
        Assert.Equal(2, b.EventCount);
    }

    [Fact]
    public void Ids_compose_by_scope()
    {
        Assert.Equal("project:tenant-1", UsageRollup.ProjectId("tenant-1"));
        Assert.Equal("board:board-1", UsageRollup.BoardId("board-1"));
        Assert.Equal("task:task-1", UsageRollup.TaskId("task-1"));
    }
}
