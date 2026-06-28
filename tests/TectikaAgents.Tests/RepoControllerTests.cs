using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Api.Services;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using Xunit;

public class RepoControllerTests
{
    private sealed class FakeRead : IGitHubReadService
    {
        public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult(new RepoMeta("main", null, false));
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult<IReadOnlyList<BranchInfo>>(new[] { new BranchInfo("main", "abc") });
        public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TreeEntry>>(System.Array.Empty<TreeEntry>());
        public Task<FileContent> GetFileAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult(new FileContent(p, "s", 0, false, ""));
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection r, string @ref, string? p, int page, CancellationToken ct) => Task.FromResult<IReadOnlyList<CommitInfo>>(System.Array.Empty<CommitInfo>());
        public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection r, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestInfo>>(System.Array.Empty<PullRequestInfo>());
        public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection r, int n, CancellationToken ct) => Task.FromResult<PullRequestInfo?>(null);
        public Task<CompareResult> CompareAsync(GitHubRepoConnection r, string b, string h, CancellationToken ct) => throw new NotImplementedException();
    }

    private static RepoController Make(Board? board, string tenant = "default")
    {
        var ctrl = new RepoController(new FakeCosmosForRepo(board), new FakeRead(), new FakeWorkspaceForRepo(), new FakeSecretsForRepo());
        var identity = new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("tid", tenant) }, "test");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [Fact]
    public async Task Branches_BoardNotFound_Returns404()
    {
        var result = await Make(null).Branches("missing", default);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Branches_NoGitHub_Returns409Typed()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = null };
        var result = await Make(board).Branches("b1", default);
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("GitHubNotConnected", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task Branches_Connected_ReturnsOkWithData()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        var result = await Make(board).Branches("b1", default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var branches = Assert.IsAssignableFrom<IReadOnlyList<BranchInfo>>(ok.Value);
        Assert.Single(branches);
    }

    [Fact]
    public async Task Branches_WrongTenant_Returns404()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        var result = await Make(board, tenant: "other").Branches("b1", default);
        Assert.IsType<NotFoundResult>(result); // board not visible to a different tenant
    }

    [Fact]
    public async Task Pull_NotFound_Returns404()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        var result = await Make(board).Pull("b1", 999, default); // FakeRead.GetPullRequestAsync returns null
        Assert.IsType<NotFoundResult>(result);
    }
}
