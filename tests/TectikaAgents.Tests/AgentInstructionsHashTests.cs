using TectikaAgents.AgentRuntime;
using Xunit;

public class AgentInstructionsHashTests
{
    [Fact]
    public void SameInputsProduceSameHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentPromptProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        var b = AgentInstructionsHash.Compute("be terse", "gpt-4o");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentModelProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o-mini");
        Assert.NotEqual(a, b);
    }
}
