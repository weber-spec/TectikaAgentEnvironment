using TectikaAgents.AgentRuntime;
using Xunit;

public class FoundryAgentNameTests
{
    [Fact]
    public void New_StartsWithAgentPrefix()
        => Assert.StartsWith("agent-", FoundryAgentName.New());

    [Fact]
    public void New_IsLowercaseAlphanumericAndHyphensOnly()
        => Assert.Matches("^[a-z0-9-]+$", FoundryAgentName.New());

    [Fact]
    public void New_IsUniqueAcrossCalls()
        => Assert.NotEqual(FoundryAgentName.New(), FoundryAgentName.New());

    [Fact]
    public void New_IsWithinFoundryLengthLimit()
    {
        var n = FoundryAgentName.New();
        Assert.InRange(n.Length, 8, 63);
    }
}
