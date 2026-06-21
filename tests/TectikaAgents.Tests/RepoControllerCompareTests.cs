using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using Xunit;

public class RepoControllerCompareTests
{
    private sealed class FakeReadWithCompare : IGitHubReadService
    {
        public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult(new RepoMeta("main", null, false));
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult<IReadOnlyList<BranchInfo>>(System.Array.Empty<BranchInfo>());
        public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TreeEntry>>(System.Array.Empty<TreeEntry>());
        public Task<FileContent> GetFileAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult(new FileContent(p, "s", 0, false, ""));
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection r, string @ref, string? p, int page, CancellationToken ct) => Task.FromResult<IReadOnlyList<CommitInfo>>(System.Array.Empty<CommitInfo>());
        public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection r, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestInfo>>(System.Array.Empty<PullRequestInfo>());
        public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection r, int n, CancellationToken ct) => Task.FromResult<PullRequestInfo?>(null);
        public Task<CompareResult> CompareAsync(GitHubRepoConnection r, string b, string h, CancellationToken ct) =>
            Task.FromResult(new CompareResult("sha", 1, 5, 1, new[] { new DiffFile("a.ts", "modified", 5, 1, false, "@@") }));
    }

    private static RepoController Make(Board? board)
    {
        var ctrl = new RepoController(new FakeCosmosForRepo(board), new FakeReadWithCompare());
        var identity = new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("tid", "default") }, "test");
        ctrl.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) } };
        return ctrl;
    }

    [Fact]
    public async Task Compare_NoGitHub_Returns409()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = null };
        Assert.IsType<ConflictObjectResult>(await Make(board).Compare("b1", "main", "agent/abc", default));
    }

    [Fact]
    public async Task Compare_MissingHead_Returns400()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        Assert.IsType<BadRequestObjectResult>(await Make(board).Compare("b1", "main", "", default));
    }

    [Fact]
    public async Task Compare_Connected_ReturnsOk()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        var ok = Assert.IsType<OkObjectResult>(await Make(board).Compare("b1", "main", "agent/abc", default));
        var result = Assert.IsType<CompareResult>(ok.Value);
        Assert.Equal(1, result.FilesChanged);
    }
}
