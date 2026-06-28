using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

public class BoardGitHubRulesTests
{
    private static Board Connected(string id, string owner, string repo) =>
        new() { Id = id, Name = id, GitHub = new GitHubRepoConnection { Owner = owner, Repo = repo } };

    [Theory]
    [InlineData("repo", "repo")]
    [InlineData("repo.git", "repo")]
    [InlineData("Repo.GIT", "Repo")]
    public void NormalizeRepo_StripsGitSuffix(string input, string expected) =>
        Assert.Equal(expected, BoardGitHubRules.NormalizeRepo(input));

    [Fact]
    public void FindConflict_DetectsSameRepoOnAnotherBoard_CaseAndGitInsensitive()
    {
        var boards = new[] { Connected("b1", "Org", "App") };
        var conflict = BoardGitHubRules.FindConflict(boards, boardId: "b2", owner: "org", repo: "app.git");
        Assert.NotNull(conflict);
        Assert.Equal("b1", conflict!.Id);
    }

    [Fact]
    public void FindConflict_AllowsSameBoardReconnect_AndDistinctRepos()
    {
        var boards = new[] { Connected("b1", "Org", "App") };
        Assert.Null(BoardGitHubRules.FindConflict(boards, "b1", "Org", "App"));
        Assert.Null(BoardGitHubRules.FindConflict(boards, "b2", "Org", "Other"));
    }

    [Fact]
    public void FindConflict_IgnoresBoardsWithoutGitHub()
    {
        var boards = new[] { new Board { Id = "b1", Name = "b1" } }; // no GitHub connection
        Assert.Null(BoardGitHubRules.FindConflict(boards, "b2", "Org", "App"));
    }
}
