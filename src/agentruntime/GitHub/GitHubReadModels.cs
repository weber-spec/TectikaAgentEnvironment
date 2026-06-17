using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

public sealed record RepoMeta(string DefaultBranch, string? Description, bool Private);
public sealed record BranchInfo(string Name, string CommitSha);
public sealed record TreeEntry(string Name, string Path, string Type, long Size); // Type: "file" | "dir"
public sealed record FileContent(string Path, string Sha, long Size, bool IsBinary, string? Text);
public sealed record CommitInfo(string Sha, string Message, string Author, DateTimeOffset Date, string Url);
public sealed record PullRequestInfo(int Number, string Title, string State, string Author, string Head, string Base, string Url, DateTimeOffset CreatedAt);

/// <summary>Read-only GitHub repository access for a board's connected repo.</summary>
public interface IGitHubReadService
{
    Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection repo, CancellationToken ct);
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection repo, CancellationToken ct);
    Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct);
    Task<FileContent> GetFileAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct);
    Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection repo, string @ref, string? path, int page, CancellationToken ct);
    Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection repo, string state, CancellationToken ct);
    Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection repo, int number, CancellationToken ct);
}
