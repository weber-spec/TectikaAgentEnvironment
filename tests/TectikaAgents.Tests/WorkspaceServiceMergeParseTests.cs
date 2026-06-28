using TectikaAgents.Workflows.Services;
using Xunit;

public class WorkspaceServiceMergeParseTests
{
    [Fact]
    public void Merged_True_IsOk()
    {
        var r = WorkspaceService.ParseMerge("{\"merged\":true,\"commit\":\"abc123\"}");
        Assert.True(r.Ok);
        Assert.Empty(r.ConflictFiles);
    }

    [Fact]
    public void Conflict_CarriesFiles_NotOk()
    {
        var r = WorkspaceService.ParseMerge("{\"merged\":false,\"conflict\":true,\"files\":[\"Game/Map.cs\",\"Program.cs\"]}");
        Assert.False(r.Ok);
        Assert.Equal(new[] { "Game/Map.cs", "Program.cs" }, r.ConflictFiles);
    }

    [Fact]
    public void Conflict_NoFilesArray_NotOk_EmptyList()
    {
        var r = WorkspaceService.ParseMerge("{\"merged\":false,\"conflict\":true}");
        Assert.False(r.Ok);
        Assert.Empty(r.ConflictFiles);
    }
}
