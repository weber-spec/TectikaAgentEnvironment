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

    private sealed class FakeCosmosForRepo : ICosmosDbService
    {
        private readonly Board? _board;
        public FakeCosmosForRepo(Board? board) => _board = board;

        // ── Bootstrap ──────────────────────────────────────────────────────────
        public Task EnsureInfrastructureAsync() => throw new NotImplementedException();

        // ── Boards ─────────────────────────────────────────────────────────────
        public Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default)
            => Task.FromResult(_board is not null && _board.TenantId == tenantId ? _board : null);
        public Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Tasks ──────────────────────────────────────────────────────────────
        public Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Agent Roles ────────────────────────────────────────────────────────
        public Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Workflow Runs ──────────────────────────────────────────────────────
        public Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Artifacts ──────────────────────────────────────────────────────────
        public Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Approvals ──────────────────────────────────────────────────────────
        public Task<Approval> CreateApprovalAsync(Approval approval, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Approval?> GetApprovalAsync(string runId, string approvalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Approval> UpdateApprovalAsync(Approval approval, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<Approval>> GetPendingApprovalsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Human Interactions ─────────────────────────────────────────────────
        public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Audit Log ──────────────────────────────────────────────────────────
        public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Edges ──────────────────────────────────────────────────────────────
        public Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();

        // ── Run trace ──────────────────────────────────────────────────────────
        public Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static RepoController Make(Board? board, string tenant = "default")
    {
        var ctrl = new RepoController(new FakeCosmosForRepo(board), new FakeRead());
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
