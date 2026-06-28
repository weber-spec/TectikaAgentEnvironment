using TectikaAgents.Api.Controllers;
using Xunit;

public class RepoWorkspaceParseTests
{
    [Fact]
    public void ParseWorkspaceTree_MapsExecutorListToTreeShape()
    {
        var json = "{\"entries\":[{\"name\":\"Game\",\"type\":\"dir\",\"size\":null},{\"name\":\"Plan.md\",\"type\":\"file\",\"size\":12}],\"path\":\"docs\"}";
        var entries = RepoController.ParseWorkspaceTree(json, "docs");
        Assert.Equal(2, entries.Count);
        Assert.Equal("Game", entries[0].Name);
        Assert.Equal("docs/Game", entries[0].Path);   // path prefixed with the dir
        Assert.Equal("dir", entries[0].Type);
        Assert.Equal("docs/Plan.md", entries[1].Path);
        Assert.Equal(12, entries[1].Size);
    }

    [Fact]
    public void ParseWorkspaceTree_RootDir_PathIsBareName()
    {
        var json = "{\"entries\":[{\"name\":\"README.md\",\"type\":\"file\",\"size\":5}],\"path\":\"\"}";
        var entries = RepoController.ParseWorkspaceTree(json, "");
        Assert.Equal("README.md", Assert.Single(entries).Path);
    }

    [Fact]
    public void ParseWorkspaceFile_MapsExecutorReadToFileShape()
    {
        var json = "{\"content\":\"# Plan\\nhello\",\"total_lines\":2,\"from_line\":1,\"to_line\":2}";
        var f = RepoController.ParseWorkspaceFile(json, "docs/Plan.md");
        Assert.Equal("docs/Plan.md", f.Path);
        Assert.Equal("# Plan\nhello", f.Text);   // raw content, no line-number decoration (S1 WYSIWYG)
        Assert.False(f.IsBinary);
    }
}
