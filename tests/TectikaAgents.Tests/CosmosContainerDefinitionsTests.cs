using TectikaAgents.Api.Services;
using Xunit;

public class CosmosContainerDefinitionsTests
{
    [Fact]
    public void Definitions_IncludeRunEvents_PartitionedByTaskId()
    {
        Assert.Contains(
            CosmosDbService.ContainerDefinitions,
            d => d.Name == "runEvents" && d.PartitionKey == "/taskId");
    }

    [Fact]
    public void Definitions_IncludePendingMessages_PartitionedByRunId()
    {
        Assert.Contains(
            CosmosDbService.ContainerDefinitions,
            d => d.Name == "pendingMessages" && d.PartitionKey == "/runId");
    }

    [Fact]
    public void Definitions_StillIncludeExistingCoreContainers()
    {
        var names = System.Linq.Enumerable.ToHashSet(
            System.Linq.Enumerable.Select(CosmosDbService.ContainerDefinitions, d => d.Name));
        Assert.Contains("tasks", names);
        Assert.Contains("artifacts", names);
        Assert.Contains("taskEdges", names);
    }
}
