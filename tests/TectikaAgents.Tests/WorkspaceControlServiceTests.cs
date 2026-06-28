using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class WorkspaceControlServiceTests
{
    private sealed class StubWorkspace : IWorkspaceService
    {
        public List<string> Destroyed { get; } = new();
        public int EnsureCalls { get; private set; }
        public bool EnsureReturnsNull;
        public Task<WorkspaceInfo?> EnsureBoardContainerAsync(Board board, CancellationToken ct = default) { EnsureCalls++; return Task.FromResult<WorkspaceInfo?>(EnsureReturnsNull ? null : new WorkspaceInfo("tws-x", "http://e:8080", "tok")); }
        public Task CreateWorktreeAsync(string e, string t, string r, string b, bool p, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveWorktreeAsync(string e, string t, string r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkspaceMergeResult> MergeRunBranchAsync(string e, string t, string r, CancellationToken ct = default) => Task.FromResult(WorkspaceMergeResult.Success());
        public Task<byte[]> BundleAsync(string e, string t, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task RestoreAsync(string e, string t, byte[] b, CancellationToken ct = default) => Task.CompletedTask;
        public Task DestroyBoardContainerAsync(string containerName, CancellationToken ct = default) { Destroyed.Add(containerName); return Task.CompletedTask; }
        public Task<CommandResult> RunCommandAsync(string e, string t, string c, int s = 60, string? r = null, CancellationToken ct = default) => Task.FromResult(new CommandResult("", "", 0));
        public Task<string> InvokeAsync(string e, string t, string route, object body, CancellationToken ct = default) => Task.FromResult("{}");
        public Task<WorkspaceAzureState> GetBoardContainerStatusAsync(string containerName, CancellationToken ct = default) => Task.FromResult(WorkspaceAzureState.Running);
    }
    private sealed class StubSecrets : ISecretProvider
    {
        public List<string> Set { get; } = new();
        public Task<string> GetSecretAsync(string name, CancellationToken ct) => Task.FromResult("tok");
        public Task SetSecretAsync(string name, string value, CancellationToken ct) { Set.Add(name); return Task.CompletedTask; }
    }

    private static (WorkspaceControlService svc, InMemoryCosmosDbService cosmos, StubWorkspace ws, StubSecrets secrets) Make()
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        var ws = new StubWorkspace();
        var secrets = new StubSecrets();
        var svc = new WorkspaceControlService(cosmos, ws, new InMemoryWorkspaceSnapshotStore(), secrets, NullLogger<WorkspaceControlService>.Instance);
        return (svc, cosmos, ws, secrets);
    }

    [Fact]
    public async Task Start_Provisions_PersistsToken_AndMarksReady()
    {
        var (svc, cosmos, ws, secrets) = Make();
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "u1" });

        var dto = await svc.StartAsync(board, default);

        Assert.Equal(1, ws.EnsureCalls);
        Assert.Contains($"workspace-token-board-{board.Id}", secrets.Set);
        var fresh = await cosmos.GetBoardAsync("t1", board.Id);
        Assert.Equal(BoardWorkspaceStatus.Ready, fresh!.WorkspaceStatus);
        Assert.Equal("tws-x", fresh.WorkspaceContainerName);
        Assert.Equal(BoardWorkspaceStatus.Ready, dto.Status);
    }

    [Fact]
    public async Task Start_RollsBackToNone_WhenProvisionFails()
    {
        var (svc, cosmos, ws, _) = Make();
        ws.EnsureReturnsNull = true;
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "u1" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(board, default));

        Assert.Equal(BoardWorkspaceStatus.None, (await cosmos.GetBoardAsync("t1", board.Id))!.WorkspaceStatus);
    }

    [Fact]
    public async Task Terminate_BlockedWhenActiveRuns()
    {
        var (svc, cosmos, ws, _) = Make();
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "u1", WorkspaceContainerName = "tws-x", WorkspaceStatus = BoardWorkspaceStatus.Ready });
        await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T", Status = AgentTaskStatus.InProgress });

        var ok = await svc.TerminateAsync(board, default);

        Assert.False(ok);
        Assert.Empty(ws.Destroyed);
    }

    [Fact]
    public async Task Terminate_DestroysAndMarksNone_WhenIdle()
    {
        var (svc, cosmos, ws, _) = Make();
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "u1", WorkspaceContainerName = "tws-x", WorkspaceStatus = BoardWorkspaceStatus.Ready });
        await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T", Status = AgentTaskStatus.Done });

        var ok = await svc.TerminateAsync(board, default);

        Assert.True(ok);
        Assert.Contains("tws-x", ws.Destroyed);
        Assert.Equal(BoardWorkspaceStatus.None, (await cosmos.GetBoardAsync("t1", board.Id))!.WorkspaceStatus);
    }
}
