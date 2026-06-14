using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class MockAgentRuntimeTests
{
    private static AgentRole Role() => new() { Id = "role-dev", DisplayName = "Dev", SystemPrompt = "code well", ModelOverride = "gpt-4o" };
    private static AgentTask Task() => new() { Id = "task-1", BoardId = "board-1", Title = "Do it" };

    [Fact]
    public async Task EnsureAgent_AssignsIdAndMarksSynced()
    {
        var p = new MockAgentProvisioner();
        var role = Role();
        var result = await p.EnsureAgentAsync(role);
        Assert.True(result.Synced);
        Assert.False(string.IsNullOrEmpty(result.FoundryAgentId));
        Assert.Null(result.Error);
        // Contract: EnsureAgentAsync mutates the role in place (the controller persists it).
        Assert.Equal(result.FoundryAgentId, role.FoundryAgentId);
        Assert.False(string.IsNullOrEmpty(role.FoundryAgentHash));
    }

    [Fact]
    public async Task EnsureThread_ReturnsStableIdForSameTask()
    {
        var rt = new MockAgentRuntime();
        var t = Task();
        var id1 = await rt.EnsureThreadAsync(t);
        var id2 = await rt.EnsureThreadAsync(t);
        Assert.False(string.IsNullOrEmpty(id1));
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task RunTurn_ReturnsCompletedWithDeterministicContentAndUsage()
    {
        var rt = new MockAgentRuntime();
        var req = new AgentRunRequest(Role(), Task(), "thread-1", "## Task: Do it", 4096, "run-123456", 0);
        var outcome = await rt.RunTurnAsync(req, new NullProjectExplorer());
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Contains("Do it", outcome.Content);
        Assert.True(outcome.TokenUsage.Input > 0);
        Assert.True(outcome.TokenUsage.Output > 0);
    }
}
