using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class GitHubRepoConnectionTests
{
    [Theory]
    [InlineData("Pacman-game.git", "Pacman-game")]   // the bug: clone-URL suffix must be stripped
    [InlineData("repo.GIT", "repo")]                 // case-insensitive
    [InlineData("Pacman-game", "Pacman-game")]       // already-clean name is untouched
    [InlineData("my.git.repo", "my.git.repo")]       // only a trailing ".git" is stripped
    public void NormalizeRepoName_strips_trailing_dotgit(string input, string expected)
    {
        Assert.Equal(expected, GitHubRepoConnection.NormalizeRepoName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeRepoName_passes_through_empty(string? input)
    {
        Assert.Equal(input, GitHubRepoConnection.NormalizeRepoName(input!));
    }
}
