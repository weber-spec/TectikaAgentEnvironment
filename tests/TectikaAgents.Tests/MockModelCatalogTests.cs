using TectikaAgents.AgentRuntime;
using Xunit;

public class MockModelCatalogTests
{
    [Fact]
    public async Task ListModelsAsync_ReturnsCanonicalStaticList()
    {
        var catalog = new MockModelCatalog();

        var models = await catalog.ListModelsAsync();

        Assert.Equal(
            new[] { "gpt-4o", "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5", "o3" },
            models);
    }
}
