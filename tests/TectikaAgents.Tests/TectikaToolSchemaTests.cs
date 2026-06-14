using System.Text.Json;
using TectikaAgents.AgentRuntime;
using Xunit;

public class TectikaToolSchemaTests
{
    [Fact]
    public void Catalog_ContainsExploreAndControlTools()
    {
        var names = TectikaToolSchema.Definitions.Select(d => d.Name).ToHashSet();
        foreach (var n in new[] { "get_board_overview", "search_tasks", "get_task", "get_artifact",
                                  "request_human_input", "request_approval", "request_revision",
                                  "update_brief", "round_intent" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void FoundryShape_IsFlatFunction()
    {
        var json = TectikaToolSchema.ToFoundryToolsJson();      // returns a JsonArray-serializable object
        var doc = JsonSerializer.SerializeToElement(json);
        var first = doc[0];
        Assert.Equal("function", first.GetProperty("type").GetString());
        Assert.True(first.TryGetProperty("name", out _));        // flat: name at top level
        Assert.True(first.TryGetProperty("parameters", out _));
        Assert.False(first.TryGetProperty("function", out _));   // NOT nested
    }

    [Fact]
    public void Version_IsStableNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(TectikaToolSchema.Version));
        Assert.Equal(TectikaToolSchema.Version, TectikaToolSchema.Version);
    }
}
