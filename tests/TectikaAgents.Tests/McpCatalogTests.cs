using System.Linq;
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class McpCatalogTests
{
    [Fact]
    public void Slack_entry_exists_with_read_and_write_tools()
    {
        var slack = McpCatalog.Find("slack");
        Assert.NotNull(slack);
        Assert.Contains(slack!.Tools, t => t.Name == "list_channels" && !t.IsWrite);
        Assert.Contains(slack.Tools, t => t.Name == "post_message" && t.IsWrite);
    }

    [Fact]
    public void Email_entry_is_first_party_with_a_write_send_tool()
    {
        var email = McpCatalog.Find("email");
        Assert.NotNull(email);
        Assert.Equal(McpBackend.FirstParty, email!.Backend);
        Assert.Contains(email.Tools, t => t.Name == "send_email" && t.IsWrite);
    }

    [Fact]
    public void Slack_entry_is_a_first_party_backend()
        => Assert.Equal(McpBackend.FirstParty, McpCatalog.Find("slack")!.Backend);

    [Fact]
    public void Find_returns_null_for_unknown_id()
        => Assert.Null(McpCatalog.Find("does-not-exist"));

    [Fact]
    public void Version_is_set()
        => Assert.False(string.IsNullOrEmpty(McpCatalog.Version));
}
