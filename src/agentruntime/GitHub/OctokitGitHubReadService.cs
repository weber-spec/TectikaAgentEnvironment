using Octokit;
using OctokitCompareResult = Octokit.CompareResult;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

public sealed class OctokitGitHubReadService : IGitHubReadService
{
    private readonly ISecretProvider _secrets;
    public OctokitGitHubReadService(ISecretProvider secrets) => _secrets = secrets;

    private Task<GitHubClient> ClientAsync(GitHubRepoConnection repo, CancellationToken ct) =>
        GitHubClientFactory.CreateAsync(_secrets, repo, ct);

    public async Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection repo, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var r = await c.Repository.Get(repo.Owner, repo.Repo);
        return new RepoMeta(r.DefaultBranch, r.Description, r.Private);
    }

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection repo, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var branches = await c.Repository.Branch.GetAll(repo.Owner, repo.Repo);
        return branches.Select(b => new BranchInfo(b.Name, b.Commit.Sha)).ToList();
    }

    public async Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        try
        {
            var items = string.IsNullOrEmpty(path)
                ? await c.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, @ref)
                : await c.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, @ref);
            return items
                .Select(i => new TreeEntry(i.Name, i.Path, i.Type.Value == ContentType.Dir ? "dir" : "file", i.Size))
                .OrderBy(e => e.Type == "file")
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (NotFoundException) { return Array.Empty<TreeEntry>(); }
    }

    public async Task<FileContent> GetFileAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var items = await c.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, @ref);
        var f = items.FirstOrDefault()
            ?? throw new NotFoundException($"File '{path}' not found on '{@ref}'.", System.Net.HttpStatusCode.NotFound);
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(f.EncodedContent, f.Size);
        return new FileContent(f.Path, f.Sha, f.Size, isBinary, text);
    }

    public async Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection repo, string @ref, string? path, int page, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var request = new CommitRequest { Sha = @ref };
        if (!string.IsNullOrEmpty(path)) request.Path = path;
        var options = new ApiOptions { PageSize = 30, PageCount = 1, StartPage = page < 1 ? 1 : page };
        var commits = await c.Repository.Commit.GetAll(repo.Owner, repo.Repo, request, options);
        return commits.Select(gc => new CommitInfo(
            gc.Sha,
            gc.Commit.Message,
            gc.Commit.Author?.Name ?? gc.Author?.Login ?? "unknown",
            gc.Commit.Author?.Date ?? default,
            gc.HtmlUrl)).ToList();
    }

    public async Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection repo, string state, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var request = new PullRequestRequest
        {
            State = state?.ToLowerInvariant() switch
            {
                "closed" => ItemStateFilter.Closed,
                "all" => ItemStateFilter.All,
                _ => ItemStateFilter.Open,
            },
        };
        var prs = await c.PullRequest.GetAllForRepository(repo.Owner, repo.Repo, request);
        return prs.Select(Map).ToList();
    }

    public async Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection repo, int number, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        try { return Map(await c.PullRequest.Get(repo.Owner, repo.Repo, number)); }
        catch (NotFoundException) { return null; }
    }

    public async Task<CompareResult> CompareAsync(GitHubRepoConnection repo, string @base, string head, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var cmp = await c.Repository.Commit.Compare(repo.Owner, repo.Repo, @base, head);
        var raw = (cmp.Files ?? new List<GitHubCommitFile>())
            .Select(f => new GitHubReadMapping.RawDiffFile(f.Filename, f.Status, f.Additions, f.Deletions, f.Patch))
            .ToList();
        var headSha = cmp.Commits is { Count: > 0 } ? cmp.Commits[^1].Sha : head;
        return GitHubReadMapping.MapCompare(headSha, raw);
    }

    private static PullRequestInfo Map(PullRequest p) => new(
        p.Number, p.Title, p.State.StringValue ?? p.State.ToString(),
        p.User?.Login ?? "unknown", p.Head?.Ref ?? "", p.Base?.Ref ?? "", p.HtmlUrl, p.CreatedAt);
}
