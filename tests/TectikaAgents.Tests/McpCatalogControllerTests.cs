using System.Linq;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using Xunit;

public class McpCatalogControllerTests
{
    [Fact]
    public void Get_returns_catalog_entries_without_endpoints()
    {
        var result = new McpCatalogController().Get() as OkObjectResult;
        Assert.NotNull(result);
        var items = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<McpCatalogController.CatalogDto>>(result!.Value);
        var slack = items.FirstOrDefault(i => i.Id == "slack");
        Assert.NotNull(slack);
        Assert.Equal("Slack", slack!.DisplayName);
        Assert.True(slack.ReadToolCount >= 1);
        Assert.True(slack.WriteToolCount >= 1);
    }
}
