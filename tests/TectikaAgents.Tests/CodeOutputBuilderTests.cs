using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class CodeOutputBuilderTests
{
    private static GitHubRepoConnection Repo() => new() { Owner = "acme", Repo = "shop", PatSecretName = "s" };
    private static CompareResult Cmp() => new("headsha", 3, 60, 8, new[]
    {
        new DiffFile("Cart.tsx", "added", 42, 0, false, "@@"),
    });

    [Fact]
    public void Build_SetsCodeKind_ExternalGithub_AndLocatorRefs()
    {
        var pr = new PullRequestInfo(12, "Checkout", "open", "agent", "agent/abc", "main", "https://gh/pr/12", System.DateTimeOffset.UtcNow);
        var o = CodeOutputBuilder.Build(Repo(), "main", "agent/abc", Cmp(), pr);

        Assert.Equal(OutputKind.Code, o.Kind);
        Assert.NotNull(o.External);
        Assert.Null(o.Inline);
        Assert.True(o.IsValid());
        Assert.Equal("github", o.External!.Provider);
        var loc = o.External.Locator;
        Assert.Equal("acme", loc["owner"]);
        Assert.Equal("shop", loc["repo"]);
        Assert.Equal("agent/abc", loc["branch"]);
        Assert.Equal("main", loc["base"]);
        Assert.Equal("headsha", loc["headSha"]);
        Assert.Equal("3", loc["filesChanged"]);
        Assert.Equal("60", loc["additions"]);
        Assert.Equal("8", loc["deletions"]);
        Assert.Equal("12", loc["prNumber"]);
        Assert.Equal("https://gh/pr/12", loc["prUrl"]);
        Assert.Equal("https://gh/pr/12", o.External.PreviewUrl);
    }

    [Fact]
    public void Build_WithoutPr_OmitsPrKeys()
    {
        var o = CodeOutputBuilder.Build(Repo(), "main", "agent/abc", Cmp(), null);
        Assert.False(o.External!.Locator.ContainsKey("prNumber"));
        Assert.False(o.External.Locator.ContainsKey("prUrl"));
        Assert.Null(o.External.PreviewUrl);
    }
}
