using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class McpToolNamingTests
{
    [Fact]
    public void Qualify_joins_with_double_underscore()
        => Assert.Equal("slack__post_message", McpToolNaming.Qualify("slack", "post_message"));

    [Fact]
    public void TryParse_splits_on_first_separator()
    {
        Assert.True(McpToolNaming.TryParse("slack__post_message", out var cid, out var tool));
        Assert.Equal("slack", cid);
        Assert.Equal("post_message", tool);
    }

    [Fact]
    public void TryParse_preserves_underscores_in_tool_name()
    {
        Assert.True(McpToolNaming.TryParse("notion__create_data_source", out var cid, out var tool));
        Assert.Equal("notion", cid);
        Assert.Equal("create_data_source", tool);
    }

    [Fact]
    public void TryParse_rejects_unqualified_name()
        => Assert.False(McpToolNaming.TryParse("read_file", out _, out _));
}
