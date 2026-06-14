using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class MockRunRoundTests
{
    private static AgentRole Role() => new() { Id = "r", DisplayName = "Dev", FoundryAgentId = "mock-agent-r" };
    private static AgentTask Task() => new() { Id = "t", BoardId = "b", Title = "Do it" };

    [Fact]
    public async Task RunRound_Continues_ThenFinalises()
    {
        var rt = new MockAgentRuntime();
        var explorer = new NullProjectExplorer();

        var r0 = await rt.RunRoundAsync(
            new RoundRequest(Role(), Task(), "thread", "seed", Array.Empty<PriorToolOutput>(), 4096, "run-1", 0), explorer);
        Assert.Equal(RoundKind.Continue, r0.Kind);
        Assert.NotEmpty(r0.NextToolOutputs);
        Assert.Equal("(mock) gathering context", r0.RoundIntent);

        var r1 = await rt.RunRoundAsync(
            new RoundRequest(Role(), Task(), "thread", null, r0.NextToolOutputs, 4096, "run-1", 1), explorer);
        Assert.Equal(RoundKind.Final, r1.Kind);
        Assert.Contains("Do it", r1.FinalText);
    }
}
