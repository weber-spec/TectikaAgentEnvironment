using TectikaAgents.AgentRuntime;
using Xunit;

public class AgentInstructionsHashToolsTests
{
    [Fact]
    public void Hash_ChangesWithToolsVersion()
    {
        var a = AgentInstructionsHash.Compute("prompt", "gpt-4o", "tools-v1");
        var b = AgentInstructionsHash.Compute("prompt", "gpt-4o", "tools-v2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_StableForSameInputs()
    {
        Assert.Equal(
            AgentInstructionsHash.Compute("p", "m", "tools-v1"),
            AgentInstructionsHash.Compute("p", "m", "tools-v1"));
    }
}
