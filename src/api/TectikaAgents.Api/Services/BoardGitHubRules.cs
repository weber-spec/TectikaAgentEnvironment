using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>Pure rules for the one-repo-per-board invariant.</summary>
public static class BoardGitHubRules
{
    public static string NormalizeRepo(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    /// <summary>The other board (≠ <paramref name="boardId"/>) already connected to owner/repo
    /// (case-insensitive, .git-insensitive), or null when the repo is free.</summary>
    public static Board? FindConflict(IEnumerable<Board> boards, string boardId, string owner, string repo)
    {
        var r = NormalizeRepo(repo);
        return boards.FirstOrDefault(b =>
            b.Id != boardId && b.GitHub is not null &&
            string.Equals(b.GitHub.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeRepo(b.GitHub.Repo), r, StringComparison.OrdinalIgnoreCase));
    }
}
