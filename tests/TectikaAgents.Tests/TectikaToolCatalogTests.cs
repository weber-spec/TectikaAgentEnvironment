using System.Linq;
using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class TectikaToolCatalogTests
{
    [Fact]
    public void Describe_groups_tools_and_locks_core()
    {
        var d = TectikaToolSchema.Describe();

        var overview = d.First(x => x.Name == "get_board_overview");
        Assert.Equal("Explore", overview.Group);
        Assert.False(overview.Lockable);                 // core Explore/Control tools are never disable-able

        var declare = d.First(x => x.Name == "declare_output");
        Assert.Equal("Control", declare.Group);
        Assert.False(declare.Lockable);

        var runCmd = d.First(x => x.Name == "run_command");
        Assert.Equal("Workspace", runCmd.Group);
        Assert.True(runCmd.Lockable);
        Assert.True(runCmd.RequiresWorkspace);

        var ghRead = d.First(x => x.Name == "github_read_file");
        Assert.Equal("GitHub", ghRead.Group);
        Assert.True(ghRead.RequiresGithubRead);
    }

    [Fact]
    public void FoundryTools_project_as_typed_tools()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(new AgentPermissions(), null, null, null,
            new[] { "code_interpreter", "web_search" });
        var json = JsonSerializer.Serialize(tools);
        Assert.Contains("\"type\":\"code_interpreter\"", json);
        Assert.Contains("\"type\":\"bing_grounding\"", json);   // web_search → bing grounding
    }

    [Fact]
    public void Unknown_foundry_tool_is_not_projected()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(new AgentPermissions(), null, null, null,
            new[] { "file_search" });                            // not agent-selectable
        Assert.DoesNotContain("file_search", JsonSerializer.Serialize(tools));
    }

    [Fact]
    public void Hash_changes_when_foundry_tools_change()
    {
        var a = AgentInstructionsHash.Compute("p", "m", "v", new AgentPermissions());
        var b = AgentInstructionsHash.Compute("p", "m", "v", new AgentPermissions(),
            foundryTools: new[] { "code_interpreter" });
        Assert.NotEqual(a, b);
    }
}
