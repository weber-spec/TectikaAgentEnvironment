using TectikaAgents.Core.Interfaces;
using TectikaAgents.Workflows.Services;
using Xunit;

public class WorkspaceServiceStateTests
{
    [Theory]
    [InlineData("Running", WorkspaceAzureState.Running)]
    [InlineData("Pending", WorkspaceAzureState.Provisioning)]
    [InlineData("Creating", WorkspaceAzureState.Provisioning)]
    [InlineData("Stopped", WorkspaceAzureState.Stopped)]
    [InlineData("Succeeded", WorkspaceAzureState.Stopped)]
    [InlineData("Terminated", WorkspaceAzureState.Stopped)]
    [InlineData("Failed", WorkspaceAzureState.Failed)]
    [InlineData("", WorkspaceAzureState.Unknown)]
    [InlineData(null, WorkspaceAzureState.Unknown)]
    [InlineData("SomethingNew", WorkspaceAzureState.Unknown)]
    public void MapAciState_ClassifiesKnownStates(string? state, WorkspaceAzureState expected) =>
        Assert.Equal(expected, WorkspaceService.MapAciState(state));
}
