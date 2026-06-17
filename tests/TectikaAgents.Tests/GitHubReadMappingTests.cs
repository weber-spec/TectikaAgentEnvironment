using System;
using System.Text;
using TectikaAgents.AgentRuntime.GitHub;
using Xunit;

public class GitHubReadMappingTests
{
    private static string B64(byte[] b) => Convert.ToBase64String(b);

    [Fact]
    public void DecodeBlob_TextContent_ReturnsTextNotBinary()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(B64(Encoding.UTF8.GetBytes("hello\nworld")), 11);
        Assert.False(isBinary);
        Assert.Equal("hello\nworld", text);
    }

    [Fact]
    public void DecodeBlob_NulByte_IsBinary_TextNull()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(B64(new byte[] { 1, 2, 0, 3 }), 4);
        Assert.True(isBinary);
        Assert.Null(text);
    }

    [Fact]
    public void DecodeBlob_OverSizeThreshold_IsBinary_TextNull()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(B64(Encoding.UTF8.GetBytes("small")), 2_000_000);
        Assert.True(isBinary);
        Assert.Null(text);
    }

    [Fact]
    public void DecodeBlob_NullOrEmptyEncoded_ReturnsEmptyText()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(null, 0);
        Assert.False(isBinary);
        Assert.Equal("", text);
    }
}
