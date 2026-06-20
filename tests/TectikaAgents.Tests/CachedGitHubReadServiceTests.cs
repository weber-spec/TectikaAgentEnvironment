using Microsoft.Extensions.Caching.Memory;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using Xunit;

public class CachedGitHubReadServiceTests
{
    private sealed class CountingInner : IGitHubReadService
    {
        public int BranchCalls;
        public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult(new RepoMeta("main", null, false));
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection r, CancellationToken ct)
        { BranchCalls++; return Task.FromResult<IReadOnlyList<BranchInfo>>(new[] { new BranchInfo("main", "abc") }); }
        public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TreeEntry>>(System.Array.Empty<TreeEntry>());
        public Task<FileContent> GetFileAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult(new FileContent(p, "s", 0, false, ""));
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection r, string @ref, string? p, int page, CancellationToken ct) => Task.FromResult<IReadOnlyList<CommitInfo>>(System.Array.Empty<CommitInfo>());
        public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection r, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestInfo>>(System.Array.Empty<PullRequestInfo>());
        public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection r, int n, CancellationToken ct) => Task.FromResult<PullRequestInfo?>(null);
        public Task<CompareResult> CompareAsync(GitHubRepoConnection r, string b, string h, CancellationToken ct) => throw new NotImplementedException();
    }

    private static GitHubRepoConnection Repo() => new() { Owner = "o", Repo = "r", PatSecretName = "s" };

    [Fact]
    public async Task SecondCall_WithinTtl_HitsCache_NotInner()
    {
        var inner = new CountingInner();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CachedGitHubReadService(inner, cache);

        await svc.ListBranchesAsync(Repo(), default);
        await svc.ListBranchesAsync(Repo(), default);

        Assert.Equal(1, inner.BranchCalls);
    }

    [Fact]
    public async Task DifferentRepo_DoesNotShareCache()
    {
        var inner = new CountingInner();
        var svc = new CachedGitHubReadService(inner, new MemoryCache(new MemoryCacheOptions()));

        await svc.ListBranchesAsync(new GitHubRepoConnection { Owner = "o", Repo = "r1", PatSecretName = "s" }, default);
        await svc.ListBranchesAsync(new GitHubRepoConnection { Owner = "o", Repo = "r2", PatSecretName = "s" }, default);

        Assert.Equal(2, inner.BranchCalls);
    }
}
