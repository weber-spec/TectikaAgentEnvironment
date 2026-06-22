using TectikaAgents.AgentRuntime.GitHub;
using Xunit;

public class CompareMappingTests
{
    [Fact]
    public void MapsFiles_SumsAdditionsDeletions_FlagsBinary()
    {
        var files = new[]
        {
            new GitHubReadMapping.RawDiffFile("a.ts", "modified", 10, 2, "@@ -1 +1 @@\n-x\n+y"),
            new GitHubReadMapping.RawDiffFile("img.png", "added", 0, 0, null),
        };
        var result = GitHubReadMapping.MapCompare("sha123", files);

        Assert.Equal("sha123", result.HeadSha);
        Assert.Equal(2, result.FilesChanged);
        Assert.Equal(10, result.Additions);
        Assert.Equal(2, result.Deletions);
        Assert.False(result.Files[0].IsBinary);
        Assert.Equal("@@ -1 +1 @@\n-x\n+y", result.Files[0].Patch);
        Assert.True(result.Files[1].IsBinary);
        Assert.Null(result.Files[1].Patch);
    }
}
