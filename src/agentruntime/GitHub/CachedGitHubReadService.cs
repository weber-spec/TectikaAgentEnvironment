using Microsoft.Extensions.Caching.Memory;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Short-TTL in-memory cache over an inner read service, to stay under
/// GitHub rate limits during interactive browsing. Keyed by repo + operation + args.</summary>
public sealed class CachedGitHubReadService : IGitHubReadService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly IGitHubReadService _inner;
    private readonly IMemoryCache _cache;

    public CachedGitHubReadService(IGitHubReadService inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    private Task<T> Cached<T>(string key, Func<Task<T>> factory) =>
        _cache.GetOrCreateAsync(key, entry => { entry.AbsoluteExpirationRelativeToNow = Ttl; return factory(); })!;

    // Key includes PatSecretName so two boards pointing at the same owner/repo with
    // different credentials never share a cache entry (private-visibility differences).
    private static string K(GitHubRepoConnection r, string op, params string[] parts) =>
        $"gh:{r.PatSecretName}:{r.Owner}/{r.Repo}:{op}:{string.Join(':', parts)}";

    public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection repo, CancellationToken ct) =>
        Cached(K(repo, "meta"), () => _inner.GetRepoMetadataAsync(repo, ct));

    public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection repo, CancellationToken ct) =>
        Cached(K(repo, "branches"), () => _inner.ListBranchesAsync(repo, ct));

    public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct) =>
        Cached(K(repo, "tree", @ref, path), () => _inner.ListDirectoryAsync(repo, @ref, path, ct));

    public Task<FileContent> GetFileAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct) =>
        Cached(K(repo, "file", @ref, path), () => _inner.GetFileAsync(repo, @ref, path, ct));

    public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection repo, string @ref, string? path, int page, CancellationToken ct) =>
        Cached(K(repo, "commits", @ref, path ?? "", page.ToString()), () => _inner.ListCommitsAsync(repo, @ref, path, page, ct));

    public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection repo, string state, CancellationToken ct) =>
        Cached(K(repo, "pulls", state), () => _inner.ListPullRequestsAsync(repo, state, ct));

    public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection repo, int number, CancellationToken ct) =>
        Cached(K(repo, "pull", number.ToString()), () => _inner.GetPullRequestAsync(repo, number, ct));

    public Task<CompareResult> CompareAsync(GitHubRepoConnection repo, string @base, string head, CancellationToken ct) =>
        Cached(K(repo, "compare", @base, head), () => _inner.CompareAsync(repo, @base, head, ct));
}
