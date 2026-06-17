using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentInstructionsHashTests
{
    private static readonly AgentPermissions DefaultPerms = new();

    [Fact]
    public void SameInputsProduceSameHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1", DefaultPerms);
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1", DefaultPerms);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentPromptProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1", DefaultPerms);
        var b = AgentInstructionsHash.Compute("be terse", "gpt-4o", "tools-v1", DefaultPerms);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentModelProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1", DefaultPerms);
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o-mini", "tools-v1", DefaultPerms);
        Assert.NotEqual(a, b);
    }
}
