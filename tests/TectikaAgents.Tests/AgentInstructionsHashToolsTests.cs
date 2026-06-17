using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentInstructionsHashToolsTests
{
    private static readonly AgentPermissions DefaultPerms = new();

    [Fact]
    public void Hash_ChangesWithToolsVersion()
    {
        var a = AgentInstructionsHash.Compute("prompt", "gpt-4o", "tools-v1", DefaultPerms);
        var b = AgentInstructionsHash.Compute("prompt", "gpt-4o", "tools-v2", DefaultPerms);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_StableForSameInputs()
    {
        Assert.Equal(
            AgentInstructionsHash.Compute("p", "m", "tools-v1", DefaultPerms),
            AgentInstructionsHash.Compute("p", "m", "tools-v1", DefaultPerms));
    }
}
