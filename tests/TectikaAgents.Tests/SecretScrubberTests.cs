using TectikaAgents.AgentRuntime;
using Xunit;

public class SecretScrubberTests
{
    [Theory]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("gho_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghs_0123456789abcdefghijklmnopqrstuvwxyz")]
    public void Redacts_classic_github_tokens(string token)
    {
        var scrubbed = SecretScrubber.Scrub($"the token is {token} ok");
        Assert.DoesNotContain(token, scrubbed);
        Assert.Contains("[REDACTED]", scrubbed);
    }

    [Fact]
    public void Redacts_fine_grained_pat()
    {
        var token = "github_pat_" + new string('A', 40) + "_" + new string('b', 30);
        var scrubbed = SecretScrubber.Scrub($"GIT_PAT={token}");
        Assert.DoesNotContain(token, scrubbed);
    }

    [Fact]
    public void Redacts_token_in_git_credentials_url()
    {
        // The exact shape written to /root/.git-credentials by the workspace entrypoint.
        var line = "https://x-access-token:ghp_0123456789abcdefghijklmnopqrstuvwxyz@github.com";
        var scrubbed = SecretScrubber.Scrub(line);
        Assert.DoesNotContain("ghp_0123456789abcdefghijklmnopqrstuvwxyz", scrubbed);
        Assert.Contains("x-access-token:[REDACTED]@", scrubbed);
    }

    [Fact]
    public void Leaves_normal_text_untouched()
    {
        const string text = "Built src/Foo.cs, ran 12 tests, all green. See task u1 for details.";
        Assert.Equal(text, SecretScrubber.Scrub(text));
    }

    [Fact]
    public void Null_or_empty_is_safe()
    {
        Assert.Equal("", SecretScrubber.Scrub(null));
        Assert.Equal("", SecretScrubber.Scrub(""));
    }
}
