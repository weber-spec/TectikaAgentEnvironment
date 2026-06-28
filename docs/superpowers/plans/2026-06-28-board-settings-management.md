# Board Settings — Workspace Control, Reset & Clone — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the board's settings dropdown with a dedicated tabbed Board Settings window that adds: workspace (ACI) view/start/restart/terminate, a destructive board "reset" (wipe work, keep plan), and board "clone" (with/without data) — plus a new one-repo-per-board rule.

**Architecture:** Backend adds two API services — `BoardMaintenanceService` (reset + clone) and `WorkspaceControlService` (status/start/restart/terminate) — over existing `ICosmosDbService`, `IWorkspaceService`, `IChatService`, and `IWorkspaceSnapshotStore`. New `BoardsController` endpoints expose them. Frontend adds a `BoardSettingsModal` (tabbed) and dialogs, with `api.boards.*` client methods. Reset/clone are synchronous; the workspace "Start" mirrors the run-attach path exactly (token persisted to KV `workspace-token-board-{boardId}`).

**Tech Stack:** .NET 8 (xUnit tests, no Moq — hand fakes + `InMemoryCosmosDbService`), Next.js 16 / React 19 (node `--experimental-strip-types --test` for pure-logic tests; UI verified via the running app).

**Spec:** `docs/superpowers/specs/2026-06-28-board-settings-management-design.md`

---

## File Structure

**Backend (create):**
- `src/api/TectikaAgents.Api/Services/BoardMaintenanceService.cs` — reset + clone orchestration.
- `src/api/TectikaAgents.Api/Services/WorkspaceControlService.cs` — workspace status DTO + start/restart/terminate.
- `src/api/TectikaAgents.Api/Services/InMemoryWorkspaceSnapshotStore.cs` — in-memory `IWorkspaceSnapshotStore` for mock mode + tests.
- `src/api/TectikaAgents.Api/Services/BoardGitHubRules.cs` — pure one-repo-per-board helper.

**Backend (modify):**
- `src/workflows/Services/WorkspaceSnapshotStore.cs` — add `DeleteAsync` to interface + blob impl.
- `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs` — add `GetBoardContainerStatusAsync` + `WorkspaceAzureState` enum.
- `src/workflows/Services/WorkspaceService.cs` — implement `GetBoardContainerStatusAsync`.
- `src/core/TectikaAgents.Core/Interfaces/ICosmosDbService.cs` — add `PurgeTaskWorkDataAsync`.
- `src/api/TectikaAgents.Api/Services/CosmosDbService.cs` — implement `PurgeTaskWorkDataAsync`.
- `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs` — implement `PurgeTaskWorkDataAsync`.
- `src/api/TectikaAgents.Api/Controllers/BoardsController.cs` — reset/clone/workspace endpoints; uniqueness in `ConnectGitHub`.
- `src/api/TectikaAgents.Api/Program.cs` — register snapshot store + two new services.

**Backend (tests):**
- `tests/TectikaAgents.Tests/BoardGitHubRulesTests.cs`
- `tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs`
- `tests/TectikaAgents.Tests/WorkspaceControlServiceTests.cs`

**Frontend (create):**
- `src/web/tectika-board/src/components/board/settings/BoardSettingsModal.tsx`
- `src/web/tectika-board/src/components/board/settings/WorkspaceTab.tsx`
- `src/web/tectika-board/src/components/board/settings/ResetBoardDialog.tsx`
- `src/web/tectika-board/src/components/board/settings/CloneBoardDialog.tsx`
- `src/web/tectika-board/src/lib/board-config.ts` (+ `src/web/tectika-board/src/lib/__tests__/board-config.test.ts`)

**Frontend (modify):**
- `src/web/tectika-board/src/lib/types.ts` — `Board` workspace fields + `BoardWorkspaceStatusDto`.
- `src/web/tectika-board/src/lib/api.ts` — `api.boards.reset/clone/workspace.*`.
- `src/web/tectika-board/src/components/board/BoardView.tsx` — open `BoardSettingsModal` from the gear; remove the dropdown.
- `src/web/tectika-board/src/lib/__tests__/board-settings-api.test.ts` (create) — api client route test.

> **Next.js note:** `src/web/tectika-board/AGENTS.md` warns this Next.js differs from training data. All new UI mirrors existing in-repo components (`Modal`, `Button`, `api.*`, `useBoard`) rather than relying on remembered Next APIs. If you reach for any Next API not already used in this codebase, read `node_modules/next/dist/docs/` first.

---

## Phase A — Backend data-layer & workspace primitives

### Task 1: `IWorkspaceSnapshotStore.DeleteAsync` + in-memory store

**Files:**
- Modify: `src/workflows/Services/WorkspaceSnapshotStore.cs`
- Create: `src/api/TectikaAgents.Api/Services/InMemoryWorkspaceSnapshotStore.cs`
- Test: `tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs` (the in-memory store is exercised there in later tasks; no standalone test needed)

- [ ] **Step 1: Add `DeleteAsync` to the interface and blob impl**

In `src/workflows/Services/WorkspaceSnapshotStore.cs`, add to the `IWorkspaceSnapshotStore` interface (after `DownloadAsync`):

```csharp
    /// <summary>Delete the board's snapshot bundle if present (best-effort, idempotent).</summary>
    Task DeleteAsync(string boardId, CancellationToken ct = default);
```

And implement in `BlobWorkspaceSnapshotStore` (after `DownloadAsync`):

```csharp
    public async Task DeleteAsync(string boardId, CancellationToken ct = default)
    {
        await _container.GetBlobClient(BlobName(boardId)).DeleteIfExistsAsync(cancellationToken: ct);
        _logger.LogInformation("[Snapshot] deleted board {BoardId} snapshot (if present)", boardId);
    }
```

- [ ] **Step 2: Create the in-memory store**

Create `src/api/TectikaAgents.Api/Services/InMemoryWorkspaceSnapshotStore.cs`:

```csharp
using System.Collections.Concurrent;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

/// <summary>In-memory <see cref="IWorkspaceSnapshotStore"/> for mock-database mode and tests —
/// no Azure Blob storage account required. State is process-local and lost on restart.</summary>
public sealed class InMemoryWorkspaceSnapshotStore : IWorkspaceSnapshotStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public Task UploadAsync(string boardId, byte[] bundle, CancellationToken ct = default)
    {
        _blobs[boardId] = bundle;
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(string boardId, CancellationToken ct = default) =>
        Task.FromResult(_blobs.TryGetValue(boardId, out var b) ? b : null);

    public Task DeleteAsync(string boardId, CancellationToken ct = default)
    {
        _blobs.TryRemove(boardId, out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Build the backend**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded (the new files compile; `BlobWorkspaceSnapshotStore` now satisfies the extended interface).

- [ ] **Step 4: Commit**

```bash
git add src/workflows/Services/WorkspaceSnapshotStore.cs src/api/TectikaAgents.Api/Services/InMemoryWorkspaceSnapshotStore.cs
git commit -m "feat(workspace): snapshot store DeleteAsync + in-memory store for mock/tests"
```

---

### Task 2: `ICosmosDbService.PurgeTaskWorkDataAsync`

Deletes all produced work for a single task: runs (+ their human interactions), artifacts, run events, usage events, and the task usage rollup. Partition keys: runs/artifacts/runEvents/usageEvents by `/taskId`; interactions by `/runId`; usage rollup by `/tenantId`.

**Files:**
- Modify: `src/core/TectikaAgents.Core/Interfaces/ICosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs`
- Test: covered by `BoardMaintenanceServiceTests` (Task 4)

- [ ] **Step 1: Add the interface method**

In `src/core/TectikaAgents.Core/Interfaces/ICosmosDbService.cs`, add (near the other task methods):

```csharp
    /// <summary>Delete ALL produced work for one task — runs (and their human interactions),
    /// artifacts, run events, usage events, and the task usage rollup. Best-effort and idempotent;
    /// used by board reset. Does NOT delete the task document itself.</summary>
    Task PurgeTaskWorkDataAsync(string tenantId, string boardId, string taskId, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `CosmosDbService`**

In `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`, add after `DeleteTaskAsync` (the `QueryAsync` helper and container constants already exist):

```csharp
    public async Task PurgeTaskWorkDataAsync(string tenantId, string boardId, string taskId, CancellationToken ct = default)
    {
        // Runs (partition /taskId) + each run's human interactions (partition /runId).
        var runs = await GetRunsByTaskAsync(taskId, ct);
        foreach (var run in runs)
        {
            try
            {
                var interactionIds = await QueryAsync<string>(HumanInteractionsContainer,
                    new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.runId = @r").WithParameter("@r", run.Id), run.Id, ct);
                foreach (var iid in interactionIds)
                    await SafeDeleteAsync(() => GetContainer(HumanInteractionsContainer)
                        .DeleteItemAsync<HumanInteraction>(iid, new PartitionKey(run.Id), cancellationToken: ct));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[Purge] interactions for run {RunId} failed", run.Id); }

            await SafeDeleteAsync(() => GetContainer(WorkflowRunsContainer)
                .DeleteItemAsync<WorkflowRun>(run.Id, new PartitionKey(taskId), cancellationToken: ct));
        }

        // Artifacts (partition /taskId).
        foreach (var a in await GetArtifactVersionsAsync(taskId, ct))
            await SafeDeleteAsync(() => GetContainer(ArtifactsContainer)
                .DeleteItemAsync<Artifact>(a.Id, new PartitionKey(taskId), cancellationToken: ct));

        // Run events (partition /taskId).
        try
        {
            var eventIds = await QueryAsync<string>(RunEventsContainer,
                new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.taskId = @t").WithParameter("@t", taskId), taskId, ct);
            foreach (var eid in eventIds)
                await SafeDeleteAsync(() => GetContainer(RunEventsContainer)
                    .DeleteItemAsync<RunEvent>(eid, new PartitionKey(taskId), cancellationToken: ct));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Purge] run events for task {TaskId} failed", taskId); }

        // Usage events (partition /taskId).
        try
        {
            var usageIds = await QueryAsync<string>(UsageEventsContainer,
                new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.taskId = @t").WithParameter("@t", taskId), taskId, ct);
            foreach (var uid in usageIds)
                await SafeDeleteAsync(() => GetContainer(UsageEventsContainer)
                    .DeleteItemAsync<UsageEvent>(uid, new PartitionKey(taskId), cancellationToken: ct));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Purge] usage events for task {TaskId} failed", taskId); }

        // Task usage rollup (partition /tenantId).
        await SafeDeleteAsync(() => GetContainer(UsageRollupsContainer)
            .DeleteItemAsync<UsageRollup>(UsageRollup.TaskId(taskId), new PartitionKey(tenantId), cancellationToken: ct));
    }

    /// <summary>Run a delete, swallowing a 404 (already gone) and logging any other failure.</summary>
    private async Task SafeDeleteAsync(Func<Task> del)
    {
        try { await del(); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        catch (Exception ex) { _logger.LogWarning(ex, "[Purge] delete failed (non-fatal)"); }
    }
```

- [ ] **Step 3: Implement in `InMemoryCosmosDbService`**

In `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs`, add after `DeleteTaskAsync`:

```csharp
    public Task PurgeTaskWorkDataAsync(string tenantId, string boardId, string taskId, CancellationToken ct = default)
    {
        var runIds = _runs.Values.Where(r => r.TaskId == taskId).Select(r => r.Id).ToHashSet();
        foreach (var id in runIds) _runs.TryRemove(id, out _);
        foreach (var i in _interactions.Values.Where(i => runIds.Contains(i.RunId)).ToList()) _interactions.TryRemove(i.Id, out _);
        foreach (var a in _artifacts.Values.Where(a => a.TaskId == taskId).ToList()) _artifacts.TryRemove(a.Id, out _);
        foreach (var e in _runEvents.Values.Where(e => e.TaskId == taskId).ToList()) _runEvents.TryRemove(e.Id, out _);
        foreach (var u in _usageEvents.Values.Where(u => u.TaskId == taskId).ToList()) _usageEvents.TryRemove(u.Id, out _);
        _usageRollups.TryRemove(UsageRollup.TaskId(taskId), out _);
        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded (both `ICosmosDbService` implementations satisfy the new member).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Interfaces/ICosmosDbService.cs src/api/TectikaAgents.Api/Services/CosmosDbService.cs src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs
git commit -m "feat(data): PurgeTaskWorkDataAsync — delete a task's runs/artifacts/events/usage"
```

---

### Task 3: `IWorkspaceService.GetBoardContainerStatusAsync`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs`
- Modify: `src/workflows/Services/WorkspaceService.cs`

- [ ] **Step 1: Add enum + interface method**

In `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs`, add the method to the interface (after `DestroyBoardContainerAsync`):

```csharp
    /// <summary>Live ACI state for the board's container group, queried from Azure Resource Manager.
    /// Returns <see cref="WorkspaceAzureState.NotFound"/> when no group exists.</summary>
    Task<WorkspaceAzureState> GetBoardContainerStatusAsync(string containerName, CancellationToken ct = default);
```

And add the enum at the bottom of the file (after the `CommandResult` record):

```csharp
public enum WorkspaceAzureState { NotFound, Provisioning, Running, Stopped, Failed, Unknown }
```

- [ ] **Step 2: Implement in `WorkspaceService`**

In `src/workflows/Services/WorkspaceService.cs`, add `using Azure;` to the usings, then add after `DestroyBoardContainerAsync`:

```csharp
    public async Task<WorkspaceAzureState> GetBoardContainerStatusAsync(string containerName, CancellationToken ct = default)
    {
        try
        {
            var arm = new ArmClient(new DefaultAzureCredential());
            var subscription = await arm.GetDefaultSubscriptionAsync(ct);
            var rg = (await subscription.GetResourceGroupAsync(_resourceGroup, ct)).Value;
            var group = (await rg.GetContainerGroupAsync(containerName, ct)).Value;
            return MapAciState(group.Data.InstanceView?.State);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return WorkspaceAzureState.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Workspace] status query for {Name} failed", containerName);
            return WorkspaceAzureState.Unknown;
        }
    }

    /// <summary>Map the ACI container-group instance-view state string to our enum. Public + static
    /// so it is unit-testable without Azure.</summary>
    public static WorkspaceAzureState MapAciState(string? state)
    {
        if (string.IsNullOrEmpty(state)) return WorkspaceAzureState.Unknown;
        var s = state.ToLowerInvariant();
        if (s.Contains("run")) return WorkspaceAzureState.Running;
        if (s.Contains("pend") || s.Contains("creat") || s.Contains("wait")) return WorkspaceAzureState.Provisioning;
        if (s.Contains("stop") || s.Contains("terminat") || s.Contains("succeed")) return WorkspaceAzureState.Stopped;
        if (s.Contains("fail") || s.Contains("error")) return WorkspaceAzureState.Failed;
        return WorkspaceAzureState.Unknown;
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs src/workflows/Services/WorkspaceService.cs
git commit -m "feat(workspace): GetBoardContainerStatusAsync — live ACI state via ARM"
```

---

## Phase B — Backend services

### Task 4: `BoardMaintenanceService.ResetBoardAsync`

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/BoardMaintenanceService.cs`
- Test: `tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs`:

```csharp
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

    private sealed class StubSecrets : ISecretProvider
    {
        public Task<string> GetSecretAsync(string name, CancellationToken ct) => Task.FromResult("tok");
        public Task SetSecretAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;
    }

    private static (BoardMaintenanceService svc, InMemoryCosmosDbService cosmos, StubWorkspace ws, InMemoryWorkspaceSnapshotStore snaps)
        Make()
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        var chat = new ChatService(cosmos, HttpFactory(), Options.Create(new DurableFunctionsSettings()),
            new SseConnectionManager(NullLogger<SseConnectionManager>.Instance), NullLogger<ChatService>.Instance);
        var ws = new StubWorkspace();
        var snaps = new InMemoryWorkspaceSnapshotStore();
        var svc = new BoardMaintenanceService(cosmos, chat, ws, snaps, new StubSecrets(), NullLogger<BoardMaintenanceService>.Instance);
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
        var t1 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T1", Status = AgentTaskStatus.Done, CurrentArtifactId = "a1", TaskBrief = "ctx" });
        var t2 = await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T2", Status = AgentTaskStatus.Review });
        await cosmos.CreateArtifactAsync(new Artifact { Id = "a1", TenantId = "t1", TaskId = t1.Id, Content = "x" });
        await cosmos.CreateEdgeAsync(new TaskEdge { Id = TaskEdge.MakeId(t1.Id, t2.Id), TenantId = "t1", BoardId = board.Id, SourceTaskId = t1.Id, TargetTaskId = t2.Id, Kind = EdgeKind.QaFeedback, CurrentIterations = 2 });
        await snaps.UploadAsync(board.Id, new byte[] { 1, 2, 3 });

        var result = await svc.ResetBoardAsync(board, clearRepo: false);

        // Items kept, all Backlog, work fields cleared.
        var tasks = (await cosmos.GetTasksByBoardAsync(board.Id)).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(AgentTaskStatus.Backlog, t.Status));
        Assert.Null((await cosmos.GetTaskAsync(board.Id, t1.Id))!.CurrentArtifactId);
        Assert.Equal("", (await cosmos.GetTaskAsync(board.Id, t1.Id))!.TaskBrief);
        // Artifacts purged.
        Assert.Empty(await cosmos.GetArtifactVersionsAsync(t1.Id));
        // Edge kept, iteration counter reset.
        var edges = (await cosmos.GetEdgesByBoardAsync(board.Id)).ToList();
        Assert.Single(edges);
        Assert.Equal(0, edges[0].CurrentIterations);
        // Workspace torn down + snapshot deleted; repo kept (clearRepo:false).
        Assert.Contains("tws-abc", ws.Destroyed);
        Assert.Null(await snaps.DownloadAsync(board.Id));
        var fresh = await cosmos.GetBoardAsync("t1", board.Id);
        Assert.Equal(BoardWorkspaceStatus.None, fresh!.WorkspaceStatus);
        Assert.Null(fresh.WorkspaceContainerName);
        Assert.NotNull(fresh.GitHub);
        Assert.Equal(2, result.TasksReset);
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
}
```

- [ ] **Step 2: Run the test to verify it fails (does not compile — service absent)**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardMaintenanceServiceTests"`
Expected: FAIL — compile error, `BoardMaintenanceService` does not exist.

- [ ] **Step 3: Create `BoardMaintenanceService` with `ResetBoardAsync`**

Create `src/api/TectikaAgents.Api/Services/BoardMaintenanceService.cs`:

```csharp
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

public sealed record ResetBoardResult(int TasksReset, int RunsCancelled, bool WorkspaceTerminated, bool RepoDisconnected);

/// <summary>Destructive board maintenance: reset (wipe produced work, keep the plan) and clone.</summary>
public sealed class BoardMaintenanceService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IChatService _chat;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSnapshotStore _snapshots;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<BoardMaintenanceService> _logger;

    public BoardMaintenanceService(ICosmosDbService cosmos, IChatService chat, IWorkspaceService workspace,
        IWorkspaceSnapshotStore snapshots, ISecretProvider secrets, ILogger<BoardMaintenanceService> logger)
    {
        _cosmos = cosmos; _chat = chat; _workspace = workspace; _snapshots = snapshots; _secrets = secrets; _logger = logger;
    }

    /// <summary>Reset the board to a clean slate: cancel active runs, delete all produced work
    /// (artifacts, runs, events, usage, files), return every item to Backlog, and tear down the ACI.
    /// Keeps items, edges (counters reset), agent roles, and views. If <paramref name="clearRepo"/>,
    /// also disconnects the GitHub remote (the remote itself is never modified).</summary>
    public async Task<ResetBoardResult> ResetBoardAsync(Board board, bool clearRepo, CancellationToken ct = default)
    {
        _logger.LogInformation("[Reset] board {BoardId} clearRepo={ClearRepo}", board.Id, clearRepo);
        var tasks = (await _cosmos.GetTasksByBoardAsync(board.Id, ct)).ToList();

        // 1. Cancel any active runs (terminates the Durable orchestration; no-op when none).
        var cancelled = 0;
        foreach (var t in tasks.Where(t => t.WorkflowRunId is not null))
            if (await _chat.StopAsync(board.Id, t.Id, ct)) cancelled++;

        // 2 + 3. Purge produced work and reset each task to Backlog.
        foreach (var t in tasks)
        {
            await _cosmos.PurgeTaskWorkDataAsync(board.TenantId, board.Id, t.Id, ct);
            t.Status = AgentTaskStatus.Backlog;
            t.WorkflowRunId = null;
            t.CurrentArtifactId = null;
            t.TaskBrief = "";
            t.ArtifactSummary = null;
            t.FoundryThreadId = null;
            t.PendingOutputs = new();
            t.HumanAskCount = 0;
            t.ChatClearedAt = null;
            t.UsageSessionId = Guid.NewGuid().ToString();
            await _cosmos.UpdateTaskAsync(t, ct);
        }

        // Reset edge loop counters (edges themselves are kept).
        foreach (var e in await _cosmos.GetEdgesByBoardAsync(board.Id, ct))
            if (e.CurrentIterations != 0) { e.CurrentIterations = 0; await _cosmos.UpdateEdgeAsync(e, ct); }

        // 4. Tear down the workspace (ACI + durable snapshot). Fresh ACI provisions on the next run.
        var wsTerminated = false;
        if (!string.IsNullOrEmpty(board.WorkspaceContainerName))
        {
            await _workspace.DestroyBoardContainerAsync(board.WorkspaceContainerName, ct);
            wsTerminated = true;
        }
        await _snapshots.DeleteAsync(board.Id, ct);
        board.WorkspaceContainerName = null;
        board.WorkspaceEndpoint = null;
        board.WorkspaceStatus = BoardWorkspaceStatus.None;
        board.WorkspaceLastUsedAt = null;

        // 5. Optionally disconnect the repo (never touches the remote).
        var repoDisconnected = false;
        if (clearRepo && board.GitHub is not null) { board.GitHub = null; repoDisconnected = true; }

        await _cosmos.UpdateBoardAsync(board, ct);
        return new ResetBoardResult(tasks.Count, cancelled, wsTerminated, repoDisconnected);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardMaintenanceServiceTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/BoardMaintenanceService.cs tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs
git commit -m "feat(boards): BoardMaintenanceService.ResetBoardAsync — wipe work, keep plan"
```

---

### Task 5: `BoardMaintenanceService.CloneBoardAsync`

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/BoardMaintenanceService.cs`
- Test: `tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `BoardMaintenanceServiceTests`:

```csharp
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
        Assert.Null(clone.GitHub);                                   // standalone
        Assert.Equal(new[] { "a", "b" }, clone.Columns.ToArray());
        var cloneTasks = (await cosmos.GetTasksByBoardAsync(clone.Id)).ToList();
        Assert.Equal(2, cloneTasks.Count);
        Assert.All(cloneTasks, t => Assert.Equal(AgentTaskStatus.Backlog, t.Status));   // no data → Backlog
        Assert.All(cloneTasks, t => Assert.Empty(cosmos.GetArtifactVersionsAsync(t.Id).Result));
        var cloneEdges = (await cosmos.GetEdgesByBoardAsync(clone.Id)).ToList();
        Assert.Single(cloneEdges);
        Assert.NotEqual(TaskEdge.MakeId(t1.Id, t2.Id), cloneEdges[0].Id);   // remapped ids
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
        Assert.Equal(AgentTaskStatus.Done, ct1.Status);             // status kept
        var arts = (await cosmos.GetArtifactVersionsAsync(ct1.Id)).ToList();
        Assert.Single(arts);                                         // latest only
        Assert.Equal("v2", arts[0].Content);
        Assert.Equal(arts[0].Id, ct1.CurrentArtifactId);
        Assert.Equal(new byte[] { 9 }, await snaps.DownloadAsync(clone.Id));   // files seeded
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardMaintenanceServiceTests"`
Expected: FAIL — `CloneBoardAsync` not defined.

- [ ] **Step 3: Implement `CloneBoardAsync`**

Add to `BoardMaintenanceService` (after `ResetBoardAsync`):

```csharp
    /// <summary>Duplicate a board as a standalone (no-GitHub) board. Always copies items, edges, and
    /// board-scoped agent roles. With <paramref name="includeData"/>: keeps item statuses, copies each
    /// task's latest artifact, and seeds the workspace from the source's snapshot blob (if any). Without:
    /// items start in Backlog with an empty workspace.</summary>
    public async Task<Board> CloneBoardAsync(Board source, string? name, bool includeData, string ownerId, CancellationToken ct = default)
    {
        _logger.LogInformation("[Clone] board {BoardId} includeData={IncludeData}", source.Id, includeData);
        var clone = await _cosmos.CreateBoardAsync(new Board
        {
            TenantId = source.TenantId,
            Name = string.IsNullOrWhiteSpace(name) ? $"Copy of {source.Name}" : name.Trim(),
            Description = source.Description,
            OwnerId = ownerId,
            Columns = new List<string>(source.Columns),
            // standalone: no GitHub, no workspace.
        }, ct);

        var sourceTasks = (await _cosmos.GetTasksByBoardAsync(source.Id, ct)).ToList();
        var idMap = sourceTasks.ToDictionary(t => t.Id, _ => Guid.NewGuid().ToString());

        foreach (var st in sourceTasks)
        {
            var nt = new AgentTask
            {
                Id = idMap[st.Id],
                TenantId = clone.TenantId,
                BoardId = clone.Id,
                Title = st.Title,
                Description = st.Description,
                Priority = st.Priority,
                Assignee = new TaskAssignee { Type = st.Assignee.Type, Id = st.Assignee.Id },
                CreatedBy = ownerId,
                Dependencies = st.Dependencies.Where(idMap.ContainsKey).Select(d => idMap[d]).ToList(),
                CanvasPosition = st.CanvasPosition is null ? null : new CanvasPosition { X = st.CanvasPosition.X, Y = st.CanvasPosition.Y },
                Prompt = st.Prompt,
                HumanAuditorId = st.HumanAuditorId,
                DueAt = st.DueAt,
                Status = includeData ? st.Status : AgentTaskStatus.Backlog,
            };

            if (includeData)
            {
                nt.ArtifactSummary = st.ArtifactSummary;
                nt.TaskBrief = st.TaskBrief;
                var latest = (await _cosmos.GetArtifactVersionsAsync(st.Id, ct)).FirstOrDefault();
                if (latest is not null)
                {
                    var copy = new Artifact
                    {
                        TenantId = clone.TenantId,
                        TaskId = nt.Id,
                        RunId = null,
                        Version = 1,
                        ContentType = latest.ContentType,
                        Content = latest.Content,
                        Summary = latest.Summary,
                        Outputs = latest.Outputs,
                        Origin = latest.Origin,
                    };
                    await _cosmos.CreateArtifactAsync(copy, ct);
                    nt.CurrentArtifactId = copy.Id;
                }
            }

            await _cosmos.CreateTaskAsync(nt, ct);
        }

        foreach (var e in await _cosmos.GetEdgesByBoardAsync(source.Id, ct))
        {
            if (!idMap.TryGetValue(e.SourceTaskId, out var ns) || !idMap.TryGetValue(e.TargetTaskId, out var nt2)) continue;
            await _cosmos.CreateEdgeAsync(new TaskEdge
            {
                Id = TaskEdge.MakeId(ns, nt2),
                TenantId = clone.TenantId,
                BoardId = clone.Id,
                SourceTaskId = ns,
                TargetTaskId = nt2,
                Kind = e.Kind,
                Label = e.Label,
                Condition = e.Condition,
                MaxIterations = e.MaxIterations,
                CurrentIterations = includeData ? e.CurrentIterations : 0,
            }, ct);
        }

        // Copy board-scoped agent roles, if any. (Agent roles are tenant-partitioned; copy only those
        // that carry this board's id. If none do, roles are tenant-shared and need no copy.)
        // NB: verify AgentRole has a BoardId before enabling this block; otherwise leave roles shared.

        if (includeData)
        {
            // Seed workspace files from the source's durable snapshot (no-repo boards keep one per run).
            // A connected source's files live in its remote, not here, so its clone may start empty —
            // documented limitation; deliverables still travel as the copied artifacts above.
            var bundle = await _snapshots.DownloadAsync(source.Id, ct);
            if (bundle is not null) await _snapshots.UploadAsync(clone.Id, bundle, ct);
        }

        return clone;
    }
```

> Note: the agent-roles copy is intentionally left as a verified-then-enabled block. Before implementing, check `src/core/TectikaAgents.Core/Models/AgentRole.cs` for a `BoardId` field. If present, copy roles whose `BoardId == source.Id` (new ids, `BoardId = clone.Id`). If absent, roles are tenant-shared and the clone references them already — leave the block as the comment.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardMaintenanceServiceTests"`
Expected: PASS (4 tests total).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/BoardMaintenanceService.cs tests/TectikaAgents.Tests/BoardMaintenanceServiceTests.cs
git commit -m "feat(boards): BoardMaintenanceService.CloneBoardAsync — duplicate with/without data"
```

---

### Task 6: `WorkspaceControlService`

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/WorkspaceControlService.cs`
- Test: `tests/TectikaAgents.Tests/WorkspaceControlServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/WorkspaceControlServiceTests.cs`:

```csharp
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
        public Task<WorkspaceInfo?> EnsureBoardContainerAsync(Board board, CancellationToken ct = default) { EnsureCalls++; return Task.FromResult<WorkspaceInfo?>(new WorkspaceInfo("tws-x", "http://e:8080", "tok")); }
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
        Assert.Contains($"workspace-token-board-{board.Id}", secrets.Set);   // run-attach contract
        var fresh = await cosmos.GetBoardAsync("t1", board.Id);
        Assert.Equal(BoardWorkspaceStatus.Ready, fresh!.WorkspaceStatus);
        Assert.Equal("tws-x", fresh.WorkspaceContainerName);
        Assert.Equal(BoardWorkspaceStatus.Ready, dto.Status);
    }

    [Fact]
    public async Task Terminate_BlockedWhenActiveRuns()
    {
        var (svc, cosmos, ws, _) = Make();
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "u1", WorkspaceContainerName = "tws-x", WorkspaceStatus = BoardWorkspaceStatus.Ready });
        await cosmos.CreateTaskAsync(new AgentTask { TenantId = "t1", BoardId = board.Id, Title = "T", Status = AgentTaskStatus.InProgress });

        var ok = await svc.TerminateAsync(board, default);

        Assert.False(ok);                       // refused
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~WorkspaceControlServiceTests"`
Expected: FAIL — `WorkspaceControlService` not defined.

- [ ] **Step 3: Implement `WorkspaceControlService`**

Create `src/api/TectikaAgents.Api/Services/WorkspaceControlService.cs`:

```csharp
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

public sealed record BoardWorkspaceStatusDto(
    BoardWorkspaceStatus Status,
    WorkspaceAzureState AzureState,
    string? ContainerName,
    string? Endpoint,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? IdleShutdownAt,
    bool HasActiveRuns,
    string Image);

/// <summary>Board-level workspace (ACI) control surfaced in Board Settings: status, start, restart,
/// terminate. Start mirrors the run-attach contract exactly — it persists the executor token to the
/// KV secret <c>workspace-token-board-{boardId}</c> so subsequent runs can attach.</summary>
public sealed class WorkspaceControlService
{
    // Mirrors IdleWorkspaceCleanupTrigger.IdleTimeout (10 min) for the shutdown-countdown display.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private const string AcrImage = "tacragentteam.azurecr.io/agent-workspace:latest";

    private readonly ICosmosDbService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSnapshotStore _snapshots;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<WorkspaceControlService> _logger;

    public WorkspaceControlService(ICosmosDbService cosmos, IWorkspaceService workspace,
        IWorkspaceSnapshotStore snapshots, ISecretProvider secrets, ILogger<WorkspaceControlService> logger)
    {
        _cosmos = cosmos; _workspace = workspace; _snapshots = snapshots; _secrets = secrets; _logger = logger;
    }

    public async Task<BoardWorkspaceStatusDto> GetStatusAsync(Board board, CancellationToken ct = default)
    {
        var azure = string.IsNullOrEmpty(board.WorkspaceContainerName)
            ? WorkspaceAzureState.NotFound
            : await _workspace.GetBoardContainerStatusAsync(board.WorkspaceContainerName, ct);
        return new BoardWorkspaceStatusDto(
            board.WorkspaceStatus,
            azure,
            board.WorkspaceContainerName,
            board.WorkspaceEndpoint,
            board.WorkspaceLastUsedAt,
            board.WorkspaceLastUsedAt is { } t ? t + IdleTimeout : null,
            await HasActiveRunsAsync(board.Id, ct),
            AcrImage);
    }

    /// <summary>Provision the board container now (without a run) and mark it Ready. Refuses if the
    /// board is already provisioning/ready (use Restart). Persists the token to KV and restores the
    /// no-repo snapshot, so a following run attaches to a fully-seeded container.</summary>
    public async Task<BoardWorkspaceStatusDto> StartAsync(Board board, CancellationToken ct = default)
    {
        if (board.WorkspaceStatus != BoardWorkspaceStatus.None)
            return await GetStatusAsync(board, ct);   // already up/coming up — idempotent

        var info = await _workspace.EnsureBoardContainerAsync(board, ct)
                   ?? throw new InvalidOperationException("Workspace provisioning returned no info.");

        // CRITICAL: persist the actual EXECUTOR_TOKEN so the run-attach path can authenticate.
        await _secrets.SetSecretAsync($"workspace-token-board-{board.Id}", info.Token, ct);

        if (board.GitHub is null)
        {
            try
            {
                var snap = await _snapshots.DownloadAsync(board.Id, ct);
                if (snap is not null) await _workspace.RestoreAsync(info.Endpoint, info.Token, snap, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[WorkspaceControl] snapshot restore failed board {BoardId} (non-fatal)", board.Id); }
        }

        board.WorkspaceContainerName = info.ContainerName;
        board.WorkspaceEndpoint = info.Endpoint;
        board.WorkspaceStatus = BoardWorkspaceStatus.Ready;
        board.WorkspaceLastUsedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpdateBoardAsync(board, ct);
        return await GetStatusAsync(board, ct);
    }

    /// <summary>Terminate the container now. Refuses (returns false) if the board has active runs.
    /// Keeps the durable snapshot so a later Start restores the files.</summary>
    public async Task<bool> TerminateAsync(Board board, CancellationToken ct = default)
    {
        if (await HasActiveRunsAsync(board.Id, ct)) return false;
        if (!string.IsNullOrEmpty(board.WorkspaceContainerName))
            await _workspace.DestroyBoardContainerAsync(board.WorkspaceContainerName, ct);
        board.WorkspaceContainerName = null;
        board.WorkspaceEndpoint = null;
        board.WorkspaceStatus = BoardWorkspaceStatus.None;
        board.WorkspaceLastUsedAt = null;
        await _cosmos.UpdateBoardAsync(board, ct);
        return true;
    }

    /// <summary>Restart = terminate (if up) then start. Refuses if active runs (via Terminate).</summary>
    public async Task<BoardWorkspaceStatusDto?> RestartAsync(Board board, CancellationToken ct = default)
    {
        if (!await TerminateAsync(board, ct)) return null;
        return await StartAsync(board, ct);
    }

    private async Task<bool> HasActiveRunsAsync(string boardId, CancellationToken ct)
    {
        var tasks = await _cosmos.GetTasksByBoardAsync(boardId, ct);
        return tasks.Any(t => t.Status is AgentTaskStatus.InProgress or AgentTaskStatus.AwaitingInteraction or AgentTaskStatus.Blocked);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~WorkspaceControlServiceTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/WorkspaceControlService.cs tests/TectikaAgents.Tests/WorkspaceControlServiceTests.cs
git commit -m "feat(workspace): WorkspaceControlService — status/start/restart/terminate"
```

---

## Phase C — Backend controller & rules

### Task 7: One-repo-per-board rule (`BoardGitHubRules` + `ConnectGitHub`)

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/BoardGitHubRules.cs`
- Modify: `src/api/TectikaAgents.Api/Controllers/BoardsController.cs`
- Test: `tests/TectikaAgents.Tests/BoardGitHubRulesTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/BoardGitHubRulesTests.cs`:

```csharp
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

public class BoardGitHubRulesTests
{
    private static Board Connected(string id, string owner, string repo) =>
        new() { Id = id, Name = id, GitHub = new GitHubRepoConnection { Owner = owner, Repo = repo } };

    [Theory]
    [InlineData("repo", "repo")]
    [InlineData("repo.git", "repo")]
    [InlineData("Repo.GIT", "Repo")]
    public void NormalizeRepo_StripsGitSuffix(string input, string expected) =>
        Assert.Equal(expected, BoardGitHubRules.NormalizeRepo(input));

    [Fact]
    public void FindConflict_DetectsSameRepoOnAnotherBoard_CaseAndGitInsensitive()
    {
        var boards = new[] { Connected("b1", "Org", "App") };
        var conflict = BoardGitHubRules.FindConflict(boards, boardId: "b2", owner: "org", repo: "app.git");
        Assert.NotNull(conflict);
        Assert.Equal("b1", conflict!.Id);
    }

    [Fact]
    public void FindConflict_AllowsSameBoardReconnect_AndDistinctRepos()
    {
        var boards = new[] { Connected("b1", "Org", "App") };
        Assert.Null(BoardGitHubRules.FindConflict(boards, "b1", "Org", "App"));   // same board
        Assert.Null(BoardGitHubRules.FindConflict(boards, "b2", "Org", "Other")); // different repo
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardGitHubRulesTests"`
Expected: FAIL — `BoardGitHubRules` not defined.

- [ ] **Step 3: Create `BoardGitHubRules`**

Create `src/api/TectikaAgents.Api/Services/BoardGitHubRules.cs`:

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>Pure rules for the one-repo-per-board invariant.</summary>
public static class BoardGitHubRules
{
    public static string NormalizeRepo(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    /// <summary>The other board (≠ <paramref name="boardId"/>) already connected to owner/repo
    /// (case-insensitive, .git-insensitive), or null when the repo is free.</summary>
    public static Board? FindConflict(IEnumerable<Board> boards, string boardId, string owner, string repo)
    {
        var r = NormalizeRepo(repo);
        return boards.FirstOrDefault(b =>
            b.Id != boardId && b.GitHub is not null &&
            string.Equals(b.GitHub.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeRepo(b.GitHub.Repo), r, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Enforce in `ConnectGitHub`**

In `src/api/TectikaAgents.Api/Controllers/BoardsController.cs`, inside `ConnectGitHub`, after the `parts.Length < 2` validation block (right before `var secretName = ...`), insert:

```csharp
        // One repo ⇄ one board (tenant-wide). Reject a repo already connected to another board.
        var allBoards = await _cosmos.GetBoardsAsync(TenantId, ct);
        var conflict = BoardGitHubRules.FindConflict(allBoards, boardId, parts[0], parts[1]);
        if (conflict is not null)
        {
            _logger.LogWarning("[GitHubConnect] board {BoardId} repo {Owner}/{Repo} already on board {OtherId}", boardId, parts[0], parts[1], conflict.Id);
            return Conflict(new { error = $"Repository {parts[0]}/{BoardGitHubRules.NormalizeRepo(parts[1])} is already connected to board \"{conflict.Name}\". A repository can be connected to only one board." });
        }
```

And normalize the stored repo name — change the `Repo = parts[1]` line in the `board.GitHub = new GitHubRepoConnection { ... }` initializer to:

```csharp
            Repo = BoardGitHubRules.NormalizeRepo(parts[1]),
```

- [ ] **Step 5: Run the tests + build**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardGitHubRulesTests"`
Expected: PASS (5 cases). Then `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj` → Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/BoardGitHubRules.cs src/api/TectikaAgents.Api/Controllers/BoardsController.cs tests/TectikaAgents.Tests/BoardGitHubRulesTests.cs
git commit -m "feat(boards): enforce one GitHub repo per board on connect"
```

---

### Task 8: `BoardsController` endpoints + DI registration

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/BoardsController.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs`

- [ ] **Step 1: Register the services + snapshot store in `Program.cs`**

In `src/api/TectikaAgents.Api/Program.cs`, immediately after the workspace-service registration (the line `builder.Services.AddSingleton<...IWorkspaceService, ...WorkspaceService>();`), add:

```csharp
// ── Workspace snapshot store (blob in prod; in-memory in mock mode) ───────────
if (useMockDatabase)
    builder.Services.AddSingleton<TectikaAgents.Workflows.Services.IWorkspaceSnapshotStore, TectikaAgents.Api.Services.InMemoryWorkspaceSnapshotStore>();
else
    builder.Services.AddSingleton<TectikaAgents.Workflows.Services.IWorkspaceSnapshotStore, TectikaAgents.Workflows.Services.BlobWorkspaceSnapshotStore>();

// ── Board maintenance (reset/clone) + workspace control ──────────────────────
builder.Services.AddScoped<TectikaAgents.Api.Services.BoardMaintenanceService>();
builder.Services.AddScoped<TectikaAgents.Api.Services.WorkspaceControlService>();
```

- [ ] **Step 2: Inject the new services into `BoardsController`**

In `src/api/TectikaAgents.Api/Controllers/BoardsController.cs`, add two fields and extend the constructor:

```csharp
    private readonly BoardMaintenanceService _maintenance;
    private readonly WorkspaceControlService _workspaceControl;

    public BoardsController(ICosmosDbService cosmos, ISecretProvider secrets, IWorkspaceService workspaceService,
        BoardMaintenanceService maintenance, WorkspaceControlService workspaceControl, ILogger<BoardsController> logger)
    {
        _cosmos = cosmos;
        _secrets = secrets;
        _workspaceService = workspaceService;
        _maintenance = maintenance;
        _workspaceControl = workspaceControl;
        _logger = logger;
    }
```

- [ ] **Step 3: Add the endpoints**

In `BoardsController.cs`, add these actions (before the closing brace of the class, after `DisconnectGitHub`):

```csharp
    // ── Workspace (ACI) control ──────────────────────────────────────────────
    [HttpGet("{boardId}/workspace")]
    public async Task<IActionResult> GetWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        return Ok(await _workspaceControl.GetStatusAsync(board, ct));
    }

    [HttpPost("{boardId}/workspace")]
    public async Task<IActionResult> StartWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        return Ok(await _workspaceControl.StartAsync(board, ct));
    }

    [HttpPost("{boardId}/workspace/restart")]
    public async Task<IActionResult> RestartWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        var dto = await _workspaceControl.RestartAsync(board, ct);
        return dto is null
            ? Conflict(new { error = "Cannot restart while the board has active runs. Stop them first." })
            : Ok(dto);
    }

    [HttpDelete("{boardId}/workspace")]
    public async Task<IActionResult> TerminateWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        var ok = await _workspaceControl.TerminateAsync(board, ct);
        return ok
            ? Ok(await _workspaceControl.GetStatusAsync(board, ct))
            : Conflict(new { error = "Cannot terminate while the board has active runs. Stop them first." });
    }

    // ── Reset (destructive) ──────────────────────────────────────────────────
    [HttpPost("{boardId}/reset")]
    public async Task<IActionResult> Reset(string boardId, [FromBody] ResetBoardRequest req, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        var result = await _maintenance.ResetBoardAsync(board, req.ClearRepo, ct);
        return Ok(result);
    }

    // ── Clone ────────────────────────────────────────────────────────────────
    [HttpPost("{boardId}/clone")]
    public async Task<IActionResult> Clone(string boardId, [FromBody] CloneBoardRequest req, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var clone = await _maintenance.CloneBoardAsync(board, req.Name, req.IncludeData, UserId, ct);
        return CreatedAtAction(nameof(Get), new { boardId = clone.Id }, clone);
    }
```

And add the request records at the bottom of the file, next to the existing records:

```csharp
public record ResetBoardRequest(bool ClearRepo);
public record CloneBoardRequest(string? Name, bool IncludeData);
```

- [ ] **Step 4: Build the API + run the full backend test suite**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded.
Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: PASS (all suites, including the existing ones, green).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/BoardsController.cs src/api/TectikaAgents.Api/Program.cs
git commit -m "feat(api): board reset/clone + workspace control endpoints"
```

---

## Phase D — Frontend api client & types

### Task 9: `Board` types + `api.boards.*` client + client test

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/lib/api.ts`
- Test: `src/web/tectika-board/src/lib/__tests__/board-settings-api.test.ts` (create)

- [ ] **Step 1: Add types**

In `src/web/tectika-board/src/lib/types.ts`, extend the `Board` interface with the workspace fields and add the status DTO type right after it:

```typescript
export interface Board {
  id: string;
  tenantId: string;
  name: string;
  description: string;
  ownerId: string;
  columns: string[];
  createdAt: string;
  github?: GitHubRepoConnection | null;
  workspaceContainerName?: string | null;
  workspaceEndpoint?: string | null;
  workspaceStatus?: 'None' | 'Provisioning' | 'Ready';
  workspaceLastUsedAt?: string | null;
}

export type WorkspaceAzureState = 'NotFound' | 'Provisioning' | 'Running' | 'Stopped' | 'Failed' | 'Unknown';

export interface BoardWorkspaceStatusDto {
  status: 'None' | 'Provisioning' | 'Ready';
  azureState: WorkspaceAzureState;
  containerName?: string | null;
  endpoint?: string | null;
  lastUsedAt?: string | null;
  idleShutdownAt?: string | null;
  hasActiveRuns: boolean;
  image: string;
}

export interface ResetBoardResult {
  tasksReset: number;
  runsCancelled: number;
  workspaceTerminated: boolean;
  repoDisconnected: boolean;
}
```

- [ ] **Step 2: Add the api methods**

In `src/web/tectika-board/src/lib/api.ts`, add to the import type list: `BoardWorkspaceStatusDto, ResetBoardResult`. Then add to the `boards: { ... }` object (after `disconnectGitHub`):

```typescript
    reset: (boardId: string, clearRepo: boolean) =>
      fetchApi<ResetBoardResult>(`/api/boards/${boardId}/reset`, {
        method: 'POST', body: JSON.stringify({ clearRepo }),
      }),
    clone: (boardId: string, opts: { name?: string; includeData: boolean }) =>
      fetchApi<Board>(`/api/boards/${boardId}/clone`, {
        method: 'POST', body: JSON.stringify({ name: opts.name, includeData: opts.includeData }),
      }),
    workspace: {
      get: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace`),
      start: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace`, { method: 'POST' }),
      restart: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace/restart`, { method: 'POST' }),
      terminate: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace`, { method: 'DELETE' }),
    },
```

- [ ] **Step 3: Write the client route test**

Create `src/web/tectika-board/src/lib/__tests__/board-settings-api.test.ts` (mirrors `preview-api.test.ts`):

```typescript
// Verifies the board reset/clone/workspace client methods build the right routes/methods/bodies.
// Run: node --test --experimental-transform-types src/lib/__tests__/board-settings-api.test.ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as nodeModule from 'node:module';

type ResolveCtx = Record<string, unknown>;
type ResolveResult = { url: string; format?: string | null };
type NextResolve = (specifier: string, context: ResolveCtx) => ResolveResult;
const registerHooks = (nodeModule as unknown as {
  registerHooks: (hooks: { resolve: (s: string, c: ResolveCtx, n: NextResolve) => ResolveResult }) => void;
}).registerHooks;
registerHooks({
  resolve(specifier: string, context: ResolveCtx, nextResolve: NextResolve): ResolveResult {
    if (/^\.\.?\//.test(specifier) && !/\.[a-z]+$/i.test(specifier)) {
      try { return nextResolve(specifier + '.ts', context); } catch { return nextResolve(specifier, context); }
    }
    return nextResolve(specifier, context);
  },
});

const { api } = await import('../api.ts');

interface Call { url: string; method: string; body?: string }
function stubFetch(): { calls: Call[]; restore: () => void } {
  const calls: Call[] = [];
  const orig = globalThis.fetch;
  globalThis.fetch = (async (url: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string | undefined });
    return new Response(JSON.stringify({ id: 'b2' }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('board settings client builds correct routes/methods/bodies', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.boards.reset('b1', true);
    await api.boards.clone('b1', { name: 'Copy', includeData: false });
    await api.boards.workspace.get('b1');
    await api.boards.workspace.start('b1');
    await api.boards.workspace.restart('b1');
    await api.boards.workspace.terminate('b1');

    assert.ok(calls[0].url.endsWith('/api/boards/b1/reset')); assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].body!, /"clearRepo":true/);
    assert.ok(calls[1].url.endsWith('/api/boards/b1/clone')); assert.equal(calls[1].method, 'POST');
    assert.match(calls[1].body!, /"includeData":false/);
    assert.ok(calls[2].url.endsWith('/api/boards/b1/workspace')); assert.equal(calls[2].method, 'GET');
    assert.ok(calls[3].url.endsWith('/api/boards/b1/workspace')); assert.equal(calls[3].method, 'POST');
    assert.ok(calls[4].url.endsWith('/api/boards/b1/workspace/restart')); assert.equal(calls[4].method, 'POST');
    assert.ok(calls[5].url.endsWith('/api/boards/b1/workspace')); assert.equal(calls[5].method, 'DELETE');
  } finally { restore(); }
});
```

- [ ] **Step 4: Run the test**

Run: `cd src/web/tectika-board && node --test --experimental-transform-types src/lib/__tests__/board-settings-api.test.ts`
Expected: PASS (1 test, 6 route assertions). If the runner flags the import hook, match the exact invocation used by the existing `preview-api.test.ts` (same folder).

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts src/web/tectika-board/src/lib/__tests__/board-settings-api.test.ts
git commit -m "feat(web): api client for board reset/clone/workspace + types"
```

---

## Phase E — Frontend settings window & features

### Task 10: `board-config.ts` localStorage clone helper (+ test)

**Files:**
- Create: `src/web/tectika-board/src/lib/board-config.ts`
- Test: `src/web/tectika-board/src/lib/__tests__/board-config.test.ts`

- [ ] **Step 1: Write the failing test**

Create `src/web/tectika-board/src/lib/__tests__/board-config.test.ts`:

```typescript
// Run: node --test --experimental-transform-types src/lib/__tests__/board-config.test.ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cloneBoardConfig } from '../board-config.ts';

function fakeLocalStorage() {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => (m.has(k) ? m.get(k)! : null),
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  } as unknown as Storage;
}

test('cloneBoardConfig copies the source board config to the new board key', () => {
  const ls = fakeLocalStorage();
  ls.setItem('tectika:board:src', JSON.stringify({ activeViewId: 'v-kanban' }));
  cloneBoardConfig('src', 'dst', ls);
  assert.equal(ls.getItem('tectika:board:dst'), JSON.stringify({ activeViewId: 'v-kanban' }));
});

test('cloneBoardConfig is a no-op when the source has no saved config', () => {
  const ls = fakeLocalStorage();
  cloneBoardConfig('src', 'dst', ls);
  assert.equal(ls.getItem('tectika:board:dst'), null);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src/web/tectika-board && node --test --experimental-transform-types src/lib/__tests__/board-config.test.ts`
Expected: FAIL — cannot find `../board-config.ts`.

- [ ] **Step 3: Implement the helper**

Create `src/web/tectika-board/src/lib/board-config.ts`:

```typescript
// Per-board UI config is persisted in localStorage under this key (mirrors board-context.tsx
// `storageKey`). Cloning a board copies the source's views/columns/etc. to the new board so the
// clone opens with the same layout.
function storageKey(boardId: string): string { return `tectika:board:${boardId}`; }

/** Copy the source board's saved UI config to the destination board key. No-op if none saved.
 * `store` defaults to window.localStorage; injectable for tests. */
export function cloneBoardConfig(srcBoardId: string, dstBoardId: string, store?: Storage): void {
  const ls = store ?? (typeof window !== 'undefined' ? window.localStorage : undefined);
  if (!ls) return;
  try {
    const raw = ls.getItem(storageKey(srcBoardId));
    if (raw) ls.setItem(storageKey(dstBoardId), raw);
  } catch { /* quota / unavailable — non-fatal */ }
}
```

- [ ] **Step 4: Run the test**

Run: `cd src/web/tectika-board && node --test --experimental-transform-types src/lib/__tests__/board-config.test.ts`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/board-config.ts src/web/tectika-board/src/lib/__tests__/board-config.test.ts
git commit -m "feat(web): cloneBoardConfig localStorage helper"
```

---

### Task 11: `CloneBoardDialog` component

**Files:**
- Create: `src/web/tectika-board/src/components/board/settings/CloneBoardDialog.tsx`

- [ ] **Step 1: Create the dialog**

Create `src/web/tectika-board/src/components/board/settings/CloneBoardDialog.tsx`:

```tsx
'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { cloneBoardConfig } from '@/lib/board-config';
import { toast } from '@/lib/toast';
import type { Board } from '@/lib/types';

export function CloneBoardDialog({ board, onClose }: { board: Board; onClose: () => void }) {
  const router = useRouter();
  const [name, setName] = useState(`Copy of ${board.name}`);
  const [includeData, setIncludeData] = useState(false);
  const [cloning, setCloning] = useState(false);

  const handleClone = async () => {
    if (!name.trim()) return;
    setCloning(true);
    try {
      const created = await api.boards.clone(board.id, { name: name.trim(), includeData });
      cloneBoardConfig(board.id, created.id);   // carry views/columns layout to the clone
      toast('Board cloned', 'success');
      router.push(`/boards/${created.id}`);
    } catch {
      toast('Could not clone board', 'error');
      setCloning(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      title="Clone board"
      width={460}
      z={1300}
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={cloning}>Cancel</Button>
          <Button variant="primary" onClick={handleClone} disabled={!name.trim() || cloning}>
            {cloning ? 'Cloning…' : 'Clone board'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-[var(--muted)]">New board name <span className="text-[#e2445c]">*</span></label>
          <input
            autoFocus
            value={name}
            onChange={e => setName(e.target.value)}
            className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)]"
          />
        </div>
        <label className="flex items-start gap-2.5 cursor-pointer select-none">
          <input type="checkbox" checked={includeData} onChange={e => setIncludeData(e.target.checked)} className="mt-0.5" />
          <span className="text-sm text-[var(--foreground)]">
            Include data
            <span className="block text-xs text-[var(--muted)]">
              Copy each item&apos;s latest deliverable and the workspace files, keeping item statuses. Off ⇒ items start in Backlog with an empty workspace.
            </span>
          </span>
        </label>
        <p className="text-[11px] text-[var(--muted)]">The clone is standalone — it is not connected to any GitHub repository.</p>
      </div>
    </Modal>
  );
}
```

- [ ] **Step 2: Verify it type-checks**

Run: `cd src/web/tectika-board && npx tsc --noEmit -p tsconfig.json`
Expected: no new errors referencing `CloneBoardDialog`. (If the project has no `tsc` step, run `npm run build` once at the end of Phase E instead.)

- [ ] **Step 3: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/CloneBoardDialog.tsx
git commit -m "feat(web): CloneBoardDialog"
```

---

### Task 12: `ResetBoardDialog` component

**Files:**
- Create: `src/web/tectika-board/src/components/board/settings/ResetBoardDialog.tsx`

- [ ] **Step 1: Create the dialog**

Create `src/web/tectika-board/src/components/board/settings/ResetBoardDialog.tsx`:

```tsx
'use client';

import { useState } from 'react';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Board } from '@/lib/types';

export function ResetBoardDialog({ board, onClose, onDone }: { board: Board; onClose: () => void; onDone: () => void }) {
  const connected = !!board.github;
  const [clearRepo, setClearRepo] = useState(false);
  const [confirm, setConfirm] = useState('');
  const [resetting, setResetting] = useState(false);
  const armed = confirm.trim() === board.name;

  const handleReset = async () => {
    if (!armed) return;
    setResetting(true);
    try {
      await api.boards.reset(board.id, connected && clearRepo);
      toast('Board reset', 'success');
      onDone();
    } catch {
      toast('Could not reset board', 'error');
      setResetting(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      title="Reset board"
      width={460}
      z={1300}
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={resetting}>Cancel</Button>
          <Button variant="primary" onClick={handleReset} disabled={!armed || resetting}
            style={{ background: '#e2445c', borderColor: '#e2445c' }}>
            {resetting ? 'Resetting…' : 'Reset board'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <p className="text-sm text-[var(--foreground)]">
          This permanently deletes <strong>all data, artifacts, run history, and workspace files</strong> for
          <strong> {board.name}</strong>, destroys its workspace container, and returns every item to Backlog.
          Items, their connections, and agent roles are kept. This cannot be undone.
        </p>
        {connected && (
          <label className="flex items-start gap-2.5 cursor-pointer select-none">
            <input type="checkbox" checked={clearRepo} onChange={e => setClearRepo(e.target.checked)} className="mt-0.5" />
            <span className="text-sm text-[var(--foreground)]">
              Also clear the repository
              <span className="block text-xs text-[var(--muted)]">
                Disconnects <strong>{board.github?.owner}/{board.github?.repo}</strong> and makes this a standalone board.
                The GitHub remote itself is not modified.
              </span>
            </span>
          </label>
        )}
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-[var(--muted)]">Type <strong>{board.name}</strong> to confirm</label>
          <input
            value={confirm}
            onChange={e => setConfirm(e.target.value)}
            className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[#e2445c]"
          />
        </div>
      </div>
    </Modal>
  );
}
```

- [ ] **Step 2: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/ResetBoardDialog.tsx
git commit -m "feat(web): ResetBoardDialog with clear-repo toggle + type-to-confirm"
```

---

### Task 13: `WorkspaceTab` component

**Files:**
- Create: `src/web/tectika-board/src/components/board/settings/WorkspaceTab.tsx`

- [ ] **Step 1: Create the tab**

Create `src/web/tectika-board/src/components/board/settings/WorkspaceTab.tsx`:

```tsx
'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Board, BoardWorkspaceStatusDto } from '@/lib/types';

// Friendly label + dot color for the live ACI state.
const STATE_UI: Record<string, { label: string; color: string }> = {
  Running:      { label: 'Running',         color: '#00c875' },
  Provisioning: { label: 'Provisioning…',   color: '#fdab3d' },
  Stopped:      { label: 'Stopped',         color: '#c4c4c4' },
  Failed:       { label: 'Failed',          color: '#e2445c' },
  NotFound:     { label: 'Not provisioned', color: '#c4c4c4' },
  Unknown:      { label: 'Unknown',         color: '#c4c4c4' },
};

export function WorkspaceTab({ board, isOwner }: { board: Board; isOwner: boolean }) {
  const [info, setInfo] = useState<BoardWorkspaceStatusDto | null>(null);
  const [busy, setBusy] = useState<'' | 'start' | 'restart' | 'terminate'>('');
  const alive = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const dto = await api.boards.workspace.get(board.id);
      if (alive.current) setInfo(dto);
    } catch { /* keep last */ }
  }, [board.id]);

  // Poll while the tab is open.
  useEffect(() => {
    alive.current = true;
    void refresh();
    const t = setInterval(refresh, 5000);
    return () => { alive.current = false; clearInterval(t); };
  }, [refresh]);

  const run = async (kind: 'start' | 'restart' | 'terminate', fn: () => Promise<BoardWorkspaceStatusDto>) => {
    setBusy(kind);
    try { setInfo(await fn()); }
    catch (e) { toast(e instanceof Error && e.message.includes('409') ? 'Stop active runs first.' : `Could not ${kind} workspace`, 'error'); }
    finally { if (alive.current) setBusy(''); }
  };

  const state = info?.azureState ?? 'Unknown';
  const ui = STATE_UI[state] ?? STATE_UI.Unknown;
  const isUp = state === 'Running' || state === 'Provisioning';

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-2.5">
        <span className="inline-flex w-2.5 h-2.5 rounded-full" style={{ background: ui.color }} />
        <span className="text-sm font-medium text-[var(--foreground)]">{ui.label}</span>
        {info?.hasActiveRuns && <span className="text-[11px] text-[var(--muted)]">· active run in progress</span>}
      </div>

      <dl className="grid grid-cols-[120px_1fr] gap-y-1.5 text-[13px]">
        <dt className="text-[var(--muted)]">Container</dt><dd className="text-[var(--foreground)] truncate">{info?.containerName ?? '—'}</dd>
        <dt className="text-[var(--muted)]">Endpoint</dt><dd className="text-[var(--foreground)] truncate">{info?.endpoint ?? '—'}</dd>
        <dt className="text-[var(--muted)]">Last used</dt><dd className="text-[var(--foreground)]">{info?.lastUsedAt ? new Date(info.lastUsedAt).toLocaleString() : '—'}</dd>
        <dt className="text-[var(--muted)]">Auto-shutdown</dt><dd className="text-[var(--foreground)]">{info?.idleShutdownAt && isUp ? new Date(info.idleShutdownAt).toLocaleTimeString() : '—'}</dd>
      </dl>

      {isOwner ? (
        <div className="flex items-center gap-2 pt-1">
          {!isUp && (
            <Button variant="primary" disabled={!!busy} onClick={() => run('start', () => api.boards.workspace.start(board.id))}>
              {busy === 'start' ? 'Starting…' : 'Start'}
            </Button>
          )}
          {isUp && (
            <Button disabled={!!busy || info?.hasActiveRuns} onClick={() => run('restart', () => api.boards.workspace.restart(board.id))}>
              {busy === 'restart' ? 'Restarting…' : 'Restart'}
            </Button>
          )}
          {isUp && (
            <Button variant="danger" disabled={!!busy || info?.hasActiveRuns} onClick={() => run('terminate', () => api.boards.workspace.terminate(board.id))}>
              {busy === 'terminate' ? 'Terminating…' : 'Terminate'}
            </Button>
          )}
          {info?.hasActiveRuns && isUp && <span className="text-[11px] text-[var(--muted)]">Stop active runs to restart/terminate.</span>}
        </div>
      ) : (
        <p className="text-[11px] text-[var(--muted)]">Only the board owner can control the workspace.</p>
      )}
      <p className="text-[11px] text-[var(--muted)]">
        The workspace is a container that holds the board&apos;s files and runs its agents. It starts automatically on
        the first run and shuts down after about 10 minutes idle.
      </p>
    </div>
  );
}
```

> If `Button` has no `variant="danger"`, it does (see `GitHubConnectModal`), so this is safe. If `tsc` flags an unknown variant, fall back to `style={{ background: '#e2445c', borderColor: '#e2445c' }}` on a default Button.

- [ ] **Step 2: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/WorkspaceTab.tsx
git commit -m "feat(web): WorkspaceTab — live ACI status + start/restart/terminate"
```

---

### Task 14: `BoardSettingsModal` + wire into `BoardView`

**Files:**
- Create: `src/web/tectika-board/src/components/board/settings/BoardSettingsModal.tsx`
- Modify: `src/web/tectika-board/src/components/board/BoardView.tsx`

- [ ] **Step 1: Create the settings modal**

Create `src/web/tectika-board/src/components/board/settings/BoardSettingsModal.tsx`:

```tsx
'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Board } from '@/lib/types';
import { GitHubConnectModal } from '@/components/board/GitHubConnectModal';
import { WorkspaceTab } from './WorkspaceTab';
import { ResetBoardDialog } from './ResetBoardDialog';
import { CloneBoardDialog } from './CloneBoardDialog';

type TabId = 'general' | 'repository' | 'workspace' | 'danger';

export function BoardSettingsModal({
  board, isOwner, onClose, onBoardUpdated,
}: {
  board: Board;
  isOwner: boolean;
  onClose: () => void;
  onBoardUpdated: (b: Board) => void;
}) {
  const router = useRouter();
  const [tab, setTab] = useState<TabId>('general');
  const [githubOpen, setGithubOpen] = useState(false);
  const [cloneOpen, setCloneOpen] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);

  // General edit state
  const [name, setName] = useState(board.name);
  const [desc, setDesc] = useState(board.description);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const tabs: { id: TabId; label: string; icon: React.ReactNode; show: boolean }[] = [
    { id: 'general',    label: 'General',    icon: <Icon.edit size={15} />,     show: true },
    { id: 'repository', label: 'Repository', icon: <Icon.branch size={15} />,   show: true },
    { id: 'workspace',  label: 'Workspace',  icon: <Icon.box size={15} />,      show: true },
    { id: 'danger',     label: 'Danger Zone',icon: <Icon.warning size={15} />,  show: isOwner },
  ].filter(t => t.show);

  const saveGeneral = async () => {
    if (!name.trim()) return;
    setSaving(true);
    try {
      const updated = await api.boards.update(board.id, name.trim(), desc.trim() || undefined);
      onBoardUpdated(updated);
      toast('Board updated', 'success');
    } catch { toast('Could not update board', 'error'); }
    finally { setSaving(false); }
  };

  const handleDelete = async () => {
    setDeleting(true);
    try {
      await api.boards.remove(board.id);
      toast('Board deleted', 'success');
      router.push('/boards');
    } catch { toast('Could not delete board', 'error'); setDeleting(false); setDeleteOpen(false); }
  };

  return (
    <>
      <Modal open onClose={onClose} title="Board settings" width={720}>
        <div className="flex gap-5 min-h-[320px]">
          {/* left nav */}
          <nav className="w-44 shrink-0 flex flex-col gap-0.5 border-r border-[var(--border)] pr-2">
            {tabs.map(t => (
              <button
                key={t.id}
                onClick={() => setTab(t.id)}
                className={`flex items-center gap-2.5 px-3 py-2 rounded-md text-[13px] text-left transition-colors ${
                  tab === t.id ? 'bg-[var(--surface)] text-[var(--foreground)] font-medium'
                               : 'text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]'
                } ${t.id === 'danger' ? 'text-[#e2445c]' : ''}`}
              >
                <span className="shrink-0">{t.icon}</span>{t.label}
              </button>
            ))}
          </nav>

          {/* panel */}
          <div className="flex-1 min-w-0">
            {tab === 'general' && (
              <div className="flex flex-col gap-4">
                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-[var(--muted)]">Board name <span className="text-[#e2445c]">*</span></label>
                  <input value={name} onChange={e => setName(e.target.value)}
                    className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)]" />
                </div>
                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-[var(--muted)]">Description</label>
                  <textarea value={desc} onChange={e => setDesc(e.target.value)} rows={3}
                    className="w-full rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)] resize-none" />
                </div>
                <div className="flex items-center justify-between pt-1">
                  <Button onClick={() => setCloneOpen(true)}><Icon.copy size={15} /> Clone board</Button>
                  <Button variant="primary" onClick={saveGeneral} disabled={!name.trim() || saving}>{saving ? 'Saving…' : 'Save'}</Button>
                </div>
              </div>
            )}

            {tab === 'repository' && (
              <div className="flex flex-col gap-3">
                {board.github ? (
                  <p className="text-sm text-[var(--foreground)]">Connected to <strong>{board.github.owner}/{board.github.repo}</strong>.</p>
                ) : (
                  <p className="text-sm text-[var(--muted)]">No repository connected. Connecting a repo lets agents push their work to GitHub. A repository can be connected to only one board.</p>
                )}
                <div><Button variant="primary" onClick={() => setGithubOpen(true)}>{board.github ? 'Manage connection' : 'Connect GitHub repo'}</Button></div>
              </div>
            )}

            {tab === 'workspace' && <WorkspaceTab board={board} isOwner={isOwner} />}

            {tab === 'danger' && isOwner && (
              <div className="flex flex-col gap-5">
                <DangerRow
                  title="Reset board"
                  desc="Delete all data, artifacts, run history, and workspace files. Items return to Backlog; connections and roles are kept."
                  action={<Button variant="danger" onClick={() => setResetOpen(true)}>Reset board</Button>}
                />
                <DangerRow
                  title="Delete board"
                  desc="Permanently delete this board and all its items. This cannot be undone."
                  action={<Button variant="danger" onClick={() => setDeleteOpen(true)}>Delete board</Button>}
                />
              </div>
            )}
          </div>
        </div>
      </Modal>

      {githubOpen && (
        <GitHubConnectModal board={board} onClose={() => setGithubOpen(false)} onUpdated={b => { onBoardUpdated(b); setGithubOpen(false); }} />
      )}
      {cloneOpen && <CloneBoardDialog board={board} onClose={() => setCloneOpen(false)} />}
      {resetOpen && <ResetBoardDialog board={board} onClose={() => setResetOpen(false)} onDone={() => { setResetOpen(false); onClose(); router.refresh(); }} />}

      {/* Delete confirm */}
      <Modal open={deleteOpen} onClose={() => setDeleteOpen(false)} title="Delete board" width={400} z={1300}
        footer={
          <>
            <Button variant="ghost" onClick={() => setDeleteOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleDelete} disabled={deleting} style={{ background: '#e2445c', borderColor: '#e2445c' }}>
              {deleting ? 'Deleting…' : 'Delete board'}
            </Button>
          </>
        }>
        <p className="text-sm text-[var(--foreground)]">This will permanently delete <strong>{board.name}</strong> and all its tasks. This cannot be undone.</p>
      </Modal>
    </>
  );
}

function DangerRow({ title, desc, action }: { title: string; desc: string; action: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-4 border border-[#e2445c33] rounded-lg p-3">
      <div className="min-w-0">
        <div className="text-sm font-medium text-[var(--foreground)]">{title}</div>
        <div className="text-xs text-[var(--muted)]">{desc}</div>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  );
}
```

> Icon names (`Icon.branch`, `Icon.box`, `Icon.copy`) may differ — open `src/web/tectika-board/src/components/ui/icons.tsx` and substitute existing members (e.g. use `Icon.settings`/`Icon.trash`/`Icon.edit` which are confirmed to exist). Do not invent icon names.

- [ ] **Step 2: Wire into `BoardView`, remove the dropdown**

In `src/web/tectika-board/src/components/board/BoardView.tsx`:

1. Add the import: `import { BoardSettingsModal } from '@/components/board/settings/BoardSettingsModal';`
2. The gear button already toggles `settingsOpen`. Replace the `<Menu .../>` element (line ~151) and the now-unused edit/delete modals with the new settings modal. Specifically, delete the `settingsOptions` array (lines ~112–118), the `<Menu anchorRef={settingsRef} ... />` line, the "Edit board modal" `<Modal>` block, and the "Delete confirm modal" `<Modal>` block. Then render, near the other modals (after `</AutomationsModal>`/GitHub block):

```tsx
      {settingsOpen && effectiveBoard && (
        <BoardSettingsModal
          board={effectiveBoard}
          isOwner={isOwner}
          onClose={() => setSettingsOpen(false)}
          onBoardUpdated={updated => { setBoardOverride(updated); setNameOverride(updated.name); setDescOverride(updated.description); }}
        />
      )}
```

3. Remove now-unused state/handlers that only served the old menu/modals: `editOpen`, `deleteOpen`, `githubOpen` (the settings modal owns GitHub now), `editName/editDesc/editSaving`, `deleting`, `openEdit`, `handleSaveEdit`, `handleDelete`, and the standalone GitHub button can stay (it can also open settings → repository, or keep opening `GitHubConnectModal`; simplest: keep the header GitHub chip but point its onClick to `setSettingsOpen(true)`). Keep `settingsOpen`, `settingsRef`, `nameOverride`, `descOverride`, `boardOverride`, `isOwner`.

> This is the one larger edit. Make it compile by removing every reference to the deleted state. Run the type-check/build (next step) and fix any "declared but never used"/"cannot find name" errors it reports — they pinpoint each leftover reference.

- [ ] **Step 3: Type-check / build the web app**

Run: `cd src/web/tectika-board && npm run build`
Expected: build completes with no type errors. Fix any unused-variable / missing-import errors surfaced by the removals.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/BoardSettingsModal.tsx src/web/tectika-board/src/components/board/BoardView.tsx
git commit -m "feat(web): tabbed Board Settings window (general/repo/workspace/danger)"
```

---

### Task 15: Full verification pass

**Files:** none (verification only)

- [ ] **Step 1: Backend — full test suite + build**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: all green (new + existing). Then `dotnet build TectikaAgents.slnx` → Build succeeded across projects.

- [ ] **Step 2: Frontend — tests, lint, build**

Run: `cd src/web/tectika-board && npm test && npm run lint && npm run build`
Expected: tests pass, no lint errors, production build succeeds.

- [ ] **Step 3: Manual smoke (mock DB) via the running-agentboard QA harness**

Follow the `running-agentboard-for-visual-qa` memory to launch the API (mock DB) + web app, then verify in the browser:
- Gear icon opens the tabbed **Board settings** window (no more dropdown).
- **General:** rename/description save; **Clone board** opens the dialog; cloning navigates to a new board that opens with the same view layout; "without data" clone shows items in Backlog and no artifacts.
- **Repository:** connect flow; connecting a repo already on another board surfaces the 409 message.
- **Workspace:** status renders (in mock/local it shows *Not provisioned*; Start will error against Azure — expected locally); buttons gate on owner + active runs.
- **Danger Zone:** Reset requires typing the board name, shows the clear-repo toggle only when connected, and after reset all items are Backlog; Delete still works.

> ACI start/terminate cannot be exercised locally (no Azure). Validate those against a deployed environment per the `manual-container-app-deploy` memory if needed.

- [ ] **Step 4: Final commit (if any verification fixes were made)**

```bash
git add -A
git commit -m "chore: board settings management — verification fixes"
```

---

## Self-Review

**Spec coverage**
- Settings window (tabbed: General/Repository/Workspace/Danger) → Tasks 13–14. ✓
- Workspace status + start/restart/terminate → Tasks 3, 6, 8, 13. ✓
- Reset (cancel runs, purge work, items→Backlog, ACI+snapshot teardown, keep edges/roles/views, clearRepo→disconnect) → Tasks 2, 4, 8, 12. ✓
- Clone (structure always; data = statuses + latest artifact + snapshot; standalone) → Tasks 1, 5, 8, 11, 10. ✓
- One repo ⇄ one board → Task 7. ✓
- localStorage view layout carried to clone → Tasks 10, 11. ✓

**Placeholder scan:** No TBD/TODO. The two "verify then enable" notes (agent-roles `BoardId`; icon names) are explicit verification steps with a concrete default, not vague gaps.

**Type consistency:** `PurgeTaskWorkDataAsync(tenantId, boardId, taskId)`, `ResetBoardAsync(board, clearRepo)`, `CloneBoardAsync(source, name, includeData, ownerId)`, `WorkspaceControlService.{GetStatusAsync,StartAsync,RestartAsync,TerminateAsync}`, `GetBoardContainerStatusAsync(containerName)`, `BoardWorkspaceStatusDto`, `ResetBoardResult`, and the `api.boards.{reset,clone,workspace.*}` names match across backend, client, and components.

**Known scope notes (carried from the spec):** reset/clone are synchronous; clone of a *connected* source seeds files only if a snapshot blob exists (deliverables still copy as artifacts); workspace Start is owner-only and persists the KV token to satisfy the run-attach contract; one-repo-per-board is not retroactive.
