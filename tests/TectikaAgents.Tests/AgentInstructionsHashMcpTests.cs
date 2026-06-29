using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentInstructionsHashMcpTests
{
    [Fact]
    public void Hash_changes_when_mcp_enabled_changes()
    {
        var perms = new AgentPermissions();
        var baseHash = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, null, null);
        var withSlack = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack" }, null);
        Assert.NotEqual(baseHash, withSlack);
    }

    [Fact]
    public void Hash_changes_when_mcp_write_optin_changes()
    {
        var perms = new AgentPermissions();
        var readOnly = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack" }, null);
        var write    = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack" }, new[] { "slack" });
        Assert.NotEqual(readOnly, write);
    }

    [Fact]
    public void Hash_is_order_independent_for_mcp_lists()
    {
        var perms = new AgentPermissions();
        var a = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack", "notion" }, null);
        var b = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "notion", "slack" }, null);
        Assert.Equal(a, b);
    }
}
