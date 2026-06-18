using TectikaAgents.Api.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class ContainerDefinitionsTests
{
    [Fact]
    public void Includes_usage_containers_with_correct_partition_keys()
    {
        var defs = CosmosDbService.ContainerDefinitions;
        Assert.Contains(defs, d => d.Name == "usageEvents" && d.PartitionKey == "/taskId");
        Assert.Contains(defs, d => d.Name == "usageRollups" && d.PartitionKey == "/tenantId");
    }
}
