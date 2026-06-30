using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using Xunit;

// Minimal ICosmosDbService fake that persists board mutations — used by this test class only.
// FakeCosmosForRepo only returns a fixed board and throws on UpdateBoardAsync, so it cannot
// support the round-trip pattern (Connect → board mutation → GetBoardAsync sees the change).
internal sealed class FakeCosmosForBoardMcp : ICosmosDbService
{
    private readonly ConcurrentDictionary<string, Board> _boards = new();

    public Task<Board> CreateBoardAsync(Board board, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(board.Id)) board.Id = Guid.NewGuid().ToString();
        _boards[board.Id] = board;
        return Task.FromResult(board);
    }

    public Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default) =>
        Task.FromResult(_boards.TryGetValue(boardId, out var b) && b.TenantId == tenantId ? b : null);

    public Task<Board> UpdateBoardAsync(Board board, CancellationToken ct = default)
    {
        _boards[board.Id] = board;
        return Task.FromResult(board);
    }

    // ── Everything below is unused in these tests ────────────────────────────
    public Task EnsureInfrastructureAsync() => throw new NotImplementedException();
    public Task<IEnumerable<Board>> GetBoardsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteBoardAsync(string tenantId, string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask> CreateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<AgentTask>> GetTasksByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask> UpdateTaskAsync(AgentTask task, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentTask?> TryClaimTaskForRunAsync(string boardId, string taskId, string runId, string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PurgeTaskWorkDataAsync(string tenantId, string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<AgentRole>> GetAgentRolesAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentRole> UpsertAgentRoleAsync(AgentRole role, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun> CreateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun?> GetRunAsync(string taskId, string runId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<WorkflowRun>> GetRunsByTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkflowRun> UpdateRunAsync(WorkflowRun run, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Artifact> CreateArtifactAsync(Artifact artifact, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<Artifact>> GetArtifactVersionsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUsageRollupAsync(UsageRollup rollup, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUsageEventAsync(UsageEvent ev, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UsageEventsPage> GetUsageEventsForTaskAsync(string tenantId, string taskId, int max, string? continuationToken, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UsageTimePoint>> GetUsageTimeSeriesAsync(string scope, string scopeId, int days, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<AgentUsage>> GetUsageByAgentAsync(string scope, string scopeId, int days, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<RunEvent>> GetRunEventsAsync(string taskId, int? sinceRound = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunEvent> CreateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunEvent> UpdateRunEventAsync(RunEvent e, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskComment> CreateCommentAsync(TaskComment comment, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskComment?> GetCommentAsync(string taskId, string commentId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TaskComment> UpsertCommentAsync(TaskComment comment, CancellationToken ct = default) => throw new NotImplementedException();
}

public class BoardMcpConnectionsControllerTests
{
    private static BoardMcpConnectionsController Build(ICosmosDbService cosmos, FakeMcpGateway gw, FakeSecretProvider secrets,
        params IFirstPartyConnector[] connectors)
    {
        var ctrl = new BoardMcpConnectionsController(cosmos, gw, secrets, connectors);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", "t1"),
            new Claim("preferred_username", "eli@tectika.com"),
        }, "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return ctrl;
    }

    // Uses FakeCosmosForBoardMcp (not InMemoryCosmosDbService — that requires an ILogger ctor arg).
    private static async Task<(ICosmosDbService cosmos, string boardId)> SeedBoardAsync()
    {
        var cosmos = new FakeCosmosForBoardMcp();
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "eli@tectika.com" });
        return (cosmos, board.Id);
    }

    [Fact]
    public async Task Connect_validates_stores_secret_and_persists_connection()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        var ctrl = Build(cosmos, gw, secrets);

        var res = await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "xoxb-abc"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(res);
        var conn = Assert.IsType<McpConnection>(ok.Value);
        Assert.Equal("slack", conn.CatalogId);
        Assert.Equal("xoxb-abc", secrets.Store[conn.SecretName]);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Single(board!.McpConnections);
    }

    [Fact]
    public async Task Connect_validation_failure_returns_400_and_stores_nothing()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway { ThrowOnList = true };
        var secrets = new FakeSecretProvider();
        var ctrl = Build(cosmos, gw, secrets);

        var res = await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "bad"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Empty(secrets.Store);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Empty(board!.McpConnections);
    }

    [Fact]
    public async Task Connect_first_party_email_validates_via_connector_not_gateway()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        var connector = new FakeFirstPartyConnector(); // CatalogId "email"
        var ctrl = Build(cosmos, gw, secrets, connector);

        var res = await ctrl.Connect(boardId,
            new BoardMcpConnectionsController.ConnectRequest("email", "Work email", "re_key"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(res);
        var conn = Assert.IsType<McpConnection>(ok.Value);
        Assert.Equal("email", conn.CatalogId);
        Assert.Equal("re_key", connector.ValidatedToken);   // validated through the connector
        Assert.Null(gw.LastTarget);                          // gateway never touched for first-party
        Assert.Equal("re_key", secrets.Store[conn.SecretName]);
    }

    [Fact]
    public async Task Connect_first_party_validation_failure_returns_400_and_stores_nothing()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var secrets = new FakeSecretProvider();
        var connector = new FakeFirstPartyConnector { ThrowOnValidate = true };
        var ctrl = Build(cosmos, new FakeMcpGateway(), secrets, connector);

        var res = await ctrl.Connect(boardId,
            new BoardMcpConnectionsController.ConnectRequest("email", "Work email", "bad"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Empty(secrets.Store);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Empty(board!.McpConnections);
    }

    [Fact]
    public async Task Connect_unknown_catalog_id_returns_400()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var ctrl = Build(cosmos, new FakeMcpGateway(), new FakeSecretProvider());
        var res = await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("nope", "x", "t"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task Disconnect_removes_connection_and_deletes_secret()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        var ctrl = Build(cosmos, gw, secrets);
        var ok = (OkObjectResult)await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "xoxb-abc"), CancellationToken.None);
        var conn = (McpConnection)ok.Value!;

        var res = await ctrl.Disconnect(boardId, conn.ConnectionId, CancellationToken.None);

        Assert.IsType<NoContentResult>(res);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Empty(board!.McpConnections);
        Assert.False(secrets.Store.ContainsKey(conn.SecretName));
    }

    [Fact]
    public async Task List_returns_board_connections()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var ctrl = Build(cosmos, new FakeMcpGateway(), new FakeSecretProvider());
        await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "xoxb-abc"), CancellationToken.None);

        var res = await ctrl.List(boardId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        var items = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<McpConnection>>(ok.Value);
        Assert.Single(items);
    }
}
