using TectikaAgents.AgentRuntime;
using Xunit;

public class AgentInstructionsHashTests
{
    [Fact]
    public void SameInputsProduceSameHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1");
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentPromptProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1");
        var b = AgentInstructionsHash.Compute("be terse", "gpt-4o", "tools-v1");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentModelProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o", "tools-v1");
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o-mini", "tools-v1");
        Assert.NotEqual(a, b);
    }
}
