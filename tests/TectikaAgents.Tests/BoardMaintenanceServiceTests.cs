using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class BoardMaintenanceServiceTests
{
    private static IHttpClientFactory HttpFactory() =>
        new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

    // Records DestroyBoardContainerAsync calls; everything else is a no-op stub.
    private sealed class StubWorkspace : IWorkspaceService
    {
        public List<string> Destroyed { get; } = new();
        public Task<WorkspaceInfo?> EnsureBoardContainerAsync(Board board, CancellationToken ct = default) =>
            Task.FromResult<WorkspaceInfo?>(new WorkspaceInfo("c", "http://e:8080", "tok"));
        public Task CreateWorktreeAsync(string e, string t, string r, string b, bool p, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveWorktreeAsync(string e, string t, string r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkspaceMergeResult> MergeRunBranchAsync(string e, string t, string r, CancellationToken ct = default) => Task.FromResult(WorkspaceMergeResult.Success());
        public Task<byte[]> BundleAsync(string e, string t, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task RestoreAsync(string e, string t, byte[] b, CancellationToken ct = default) => Task.CompletedTask;
        public Task DestroyBoardContainerAsync(string containerName, CancellationToken ct = default) { Destroyed.Add(containerName); return Task.CompletedTask; }
        public Task<CommandResult> RunCommandAsync(string e, string t, string c, int s = 60, string? r = null, CancellationToken ct = default) => Task.FromResult(new CommandResult("", "", 0));
        public Task<string> InvokeAsync(string e, string t, string route, object body, CancellationToken ct = default) => Task.FromResult("{}");
        public Task<WorkspaceAzureState> GetBoardContainerStatusAsync(string containerName, CancellationToken ct = default) => Task.FromResult(WorkspaceAzureState.NotFound);
    }

    private static (BoardMaintenanceService svc, InMemoryCosmosDbService cosmos, StubWorkspace ws, InMemoryWorkspaceSnapshotStore snaps)
        Make()
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        var chat = new ChatService(cosmos, HttpFactory(), Options.Create(new DurableFunctionsSettings()),
            new SseConnectionManager(NullLogger<SseConnectionManager>.Instance), NullLogger<ChatService>.Instance);
        var ws = new StubWorkspace();
        var snaps = new InMemoryWorkspaceSnapshotStore();
        var svc = new BoardMaintenanceService(cosmos, chat, ws, snaps, NullLogger<BoardMaintenanceService>.Instance);
        return (svc, cosmos, ws, snaps);
    }

    [Fact]
    public async Task ResetBoard_WipesWork_KeepsItemsAndEdges_AndTearsDownWorkspace()
    {
        var (svc, cosmos, ws, snaps) = Make();
        var board = await cosmos.CreateBoardAsync(new Board {
            TenantId = "t1", Name = "B", OwnerId = "u1",
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "s" },
            WorkspaceContainerName = "tws-abc", WorkspaceStatus = BoardWorkspaceStatus.Ready,
        });
        var t1 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T1", Status = AgentTaskStatus.Done, CurrentArtifactId = "a1", TaskBrief = "ctx", WorkflowRunId = "r1", FoundryThreadId = "th" });
        var t2 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T2", Status = AgentTaskStatus.Review });
        await cosmos.CreateArtifactAsync(new Artifact { Id = "a1", TenantId = "t1", TaskId = t1.Id, Content = "x" });
        await cosmos.CreateEdgeAsync(new TaskEdge { Id = TaskEdge.MakeId(t1.Id, t2.Id), TenantId = "t1", BoardId = board.Id, SourceTaskId = t1.Id, TargetTaskId = t2.Id, Kind = EdgeKind.QaFeedback, CurrentIterations = 2 });
        await snaps.UploadAsync(board.Id, new byte[] { 1, 2, 3 });

        var result = await svc.ResetBoardAsync(board, clearRepo: false);

        var tasks = (await cosmos.GetTasksByBoardAsync(board.Id)).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(AgentTaskStatus.Backlog, t.Status));
        Assert.Null((await cosmos.GetTaskAsync(board.Id, t1.Id))!.CurrentArtifactId);
        Assert.Equal("", (await cosmos.GetTaskAsync(board.Id, t1.Id))!.TaskBrief);
        Assert.Empty(await cosmos.GetArtifactVersionsAsync(t1.Id));
        var edges = (await cosmos.GetEdgesByBoardAsync(board.Id)).ToList();
        Assert.Single(edges);
        Assert.Equal(0, edges[0].CurrentIterations);
        Assert.Contains("tws-abc", ws.Destroyed);
        Assert.Null(await snaps.DownloadAsync(board.Id));
        var fresh = await cosmos.GetBoardAsync("t1", board.Id);
        Assert.Equal(BoardWorkspaceStatus.None, fresh!.WorkspaceStatus);
        Assert.Null(fresh.WorkspaceContainerName);
        Assert.NotNull(fresh.GitHub);
        Assert.Equal(2, result.TasksReset);
        Assert.True(result.WorkspaceTerminated);
        Assert.Equal(0, result.RunsCancelled);   // no live durable orchestration in a unit test
        Assert.Null((await cosmos.GetTaskAsync(board.Id, t1.Id))!.WorkflowRunId);
        Assert.Null((await cosmos.GetTaskAsync(board.Id, t1.Id))!.FoundryThreadId);
    }

    [Fact]
    public async Task ResetBoard_ClearRepo_DisconnectsGitHub()
    {
        var (svc, cosmos, _, _) = Make();
        var board = await cosmos.CreateBoardAsync(new Board {
            TenantId = "t1", Name = "B", OwnerId = "u1",
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "s" },
        });
        var result = await svc.ResetBoardAsync(board, clearRepo: true);
        Assert.Null((await cosmos.GetBoardAsync("t1", board.Id))!.GitHub);
        Assert.True(result.RepoDisconnected);
    }

    [Fact]
    public async Task Clone_WithoutData_CopiesStructure_ItemsToBacklog_NoArtifacts_Standalone()
    {
        var (svc, cosmos, _, _) = Make();
        var src = await cosmos.CreateBoardAsync(new Board {
            TenantId = "t1", Name = "Src", OwnerId = "u1", Columns = new() { "a", "b" },
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "s" },
        });
        var t1 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = src.Id, Title = "T1", Status = AgentTaskStatus.Done });
        var t2 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = src.Id, Title = "T2", Status = AgentTaskStatus.Review });
        await cosmos.CreateArtifactAsync(new Artifact { TenantId = "t1", TaskId = t1.Id, Content = "x" });
        await cosmos.CreateEdgeAsync(new TaskEdge { Id = TaskEdge.MakeId(t1.Id, t2.Id), TenantId = "t1", BoardId = src.Id, SourceTaskId = t1.Id, TargetTaskId = t2.Id });

        var clone = await svc.CloneBoardAsync(src, name: null, includeData: false, ownerId: "u2");

        Assert.NotEqual(src.Id, clone.Id);
        Assert.Equal("Copy of Src", clone.Name);
        Assert.Equal("u2", clone.OwnerId);
        Assert.Null(clone.GitHub);
        Assert.Equal(new[] { "a", "b" }, clone.Columns.ToArray());
        var cloneTasks = (await cosmos.GetTasksByBoardAsync(clone.Id)).ToList();
        Assert.Equal(2, cloneTasks.Count);
        Assert.All(cloneTasks, t => Assert.Equal(AgentTaskStatus.Backlog, t.Status));
        foreach (var t in cloneTasks)
            Assert.Empty(await cosmos.GetArtifactVersionsAsync(t.Id));
        var cloneEdges = (await cosmos.GetEdgesByBoardAsync(clone.Id)).ToList();
        Assert.Single(cloneEdges);
        Assert.NotEqual(TaskEdge.MakeId(t1.Id, t2.Id), cloneEdges[0].Id);
    }

    [Fact]
    public async Task Clone_WithData_KeepsStatuses_CopiesLatestArtifact_AndSnapshot()
    {
        var (svc, cosmos, _, snaps) = Make();
        var src = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "Src", OwnerId = "u1" });
        var t1 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = src.Id, Title = "T1", Status = AgentTaskStatus.Done });
        await cosmos.CreateArtifactAsync(new Artifact { TenantId = "t1", TaskId = t1.Id, Version = 1, Content = "v1" });
        await cosmos.CreateArtifactAsync(new Artifact { TenantId = "t1", TaskId = t1.Id, Version = 2, Content = "v2" });
        await snaps.UploadAsync(src.Id, new byte[] { 9 });

        var clone = await svc.CloneBoardAsync(src, name: "My Copy", includeData: true, ownerId: "u2");

        Assert.Equal("My Copy", clone.Name);
        var ct1 = (await cosmos.GetTasksByBoardAsync(clone.Id)).Single();
        Assert.Equal(AgentTaskStatus.Done, ct1.Status);
        var arts = (await cosmos.GetArtifactVersionsAsync(ct1.Id)).ToList();
        Assert.Single(arts);
        Assert.Equal("v2", arts[0].Content);
        Assert.Equal(arts[0].Id, ct1.CurrentArtifactId);
        Assert.Equal(new byte[] { 9 }, await snaps.DownloadAsync(clone.Id));
    }
}
