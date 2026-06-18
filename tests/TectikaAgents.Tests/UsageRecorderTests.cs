using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class UsageRecorderTests
{
    [Fact]
    public void Apply_increments_lifetime_and_perModel()
    {
        var rollup = new UsageRollup { Id = UsageRollup.BoardId("b1"), TenantId = "t1", Scope = UsageScope.Board, ScopeId = "b1" };
        var usage = new TokenUsage { Input = 100, Output = 50 };

        UsageRecorder.ApplyShared(rollup, "azure-foundry", "gpt-4o", usage, 0.5m);

        Assert.Equal(100, rollup.Lifetime.Tokens.Input);
        Assert.Equal(0.5m, rollup.Lifetime.CostUsd);
        Assert.Equal(1, rollup.Lifetime.EventCount);
        Assert.True(rollup.PerModel.ContainsKey("azure-foundry/gpt-4o"));
        Assert.Equal(50, rollup.PerModel["azure-foundry/gpt-4o"].Tokens.Output);
    }

    [Fact]
    public void Apply_to_task_also_updates_currentSession_when_matching()
    {
        var rollup = new UsageRollup
        {
            Id = UsageRollup.TaskId("k1"), TenantId = "t1", Scope = UsageScope.Task, ScopeId = "k1",
            CurrentSession = new SessionBucket { SessionId = "sess-1" }
        };
        var usage = new TokenUsage { Input = 10, Output = 5 };

        UsageRecorder.ApplyTask(rollup, "azure-foundry", "gpt-4o", usage, 0.1m, "sess-1");

        Assert.Equal(10, rollup.Lifetime.Tokens.Input);
        Assert.Equal(5, rollup.CurrentSession!.Tokens.Output);
        Assert.Equal(1, rollup.CurrentSession.EventCount);
    }

    [Fact]
    public void Apply_to_task_starts_new_session_bucket_when_session_changed()
    {
        var rollup = new UsageRollup
        {
            Id = UsageRollup.TaskId("k1"), TenantId = "t1", Scope = UsageScope.Task, ScopeId = "k1",
            CurrentSession = new SessionBucket { SessionId = "old" }
        };
        UsageRecorder.ApplyTask(rollup, "azure-foundry", "gpt-4o", new TokenUsage { Input = 7 }, 0.0m, "new");

        Assert.Equal("new", rollup.CurrentSession!.SessionId);
        Assert.Equal(7, rollup.CurrentSession.Tokens.Input);   // fresh bucket, not old + 7
        Assert.Equal(7, rollup.Lifetime.Tokens.Input);          // lifetime always accumulates
    }
}
