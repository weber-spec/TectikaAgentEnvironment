# Phase 0 — Typed Edges Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `TaskEdge` the single, server-persisted, typed source of truth for canvas edges (kind/label/condition/maxIterations), removing the `upstream/downstream` task arrays and migrating every consumer.

**Architecture:** New `taskEdges` Cosmos container (PK `/boardId`) behind `ICosmosDbService`; a board-scoped `EdgesController` with server-side kind auto-detection; the workflow context code and the Next.js canvas read/write edges; seed emits edge docs and a one-time backfill converts existing data.

**Tech Stack:** .NET 10 (ASP.NET Core API, Azure Cosmos SDK 3.60 w/ System.Text.Json serializer), Next.js 16 / React 19 / TypeScript, @xyflow/react canvas.

**Verification model (this repo has no unit-test harness):** each backend task is verified by `dotnet build` (compile) **and**, where behavioral, by running the API in mock mode and asserting with `curl`. Frontend tasks are verified by `npm run build` + `npm run lint` and a manual canvas check. Commit after each green task.

How to run the API in mock mode for curl checks (used repeatedly below):
```bash
# from repo root — mock DB + dev auth, no Azure needed
cd src/api/TectikaAgents.Api
MockDatabase__Enabled=true DevAuth__Enabled=true ASPNETCORE_ENVIRONMENT=Development \
  dotnet run -c Release --urls http://localhost:5000
# (run in a background shell; curl localhost:5000; stop when done)
```

---

## File structure

**Create**
- `src/core/TectikaAgents.Core/Models/TaskEdge.cs` — the edge entity + `EdgeKind` enum.
- `src/api/TectikaAgents.Api/Controllers/EdgesController.cs` — board-scoped CRUD.
- `src/api/TectikaAgents.Api/Services/EdgeKindDetector.cs` — reachability-based kind auto-detect (pure, testable).
- `src/api/TectikaAgents.Api/Services/MockData/EdgeBackfill.cs` — one-time arrays→edges backfill.

**Modify**
- `src/api/.../Services/ICosmosDbService.cs`, `CosmosDbService.cs`, `InMemoryCosmosDbService.cs` — edge container + methods.
- `src/api/.../Controllers/TasksController.cs` — remove `/connect` + array patch; cascade edge delete on task delete.
- `src/api/.../Services/MockData/MockDataSeeder.cs`, `CosmosDataSeeder.cs` — emit edges, stop setting arrays.
- `src/api/.../Program.cs` — `--backfill-edges` mode.
- `src/core/.../Models/AgentTask.cs` — remove the arrays.
- `src/workflows/Services/WorkflowCosmosService.cs`, `Activities/InvokeAgentActivity.cs` — upstream from edges.
- `src/web/tectika-board/src/lib/types.ts`, `api.ts`, `board-context.tsx`, `components/board/canvas/CanvasView.tsx` (+ consumer audit).

---

## Task 1: TaskEdge model

**Files:** Create `src/core/TectikaAgents.Core/Models/TaskEdge.cs`

- [ ] **Step 1: Create the model** (mirror the style of `AgentTask.cs` — STJ attributes, camelCase, defaults)

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum EdgeKind { Dependency, QaFeedback }

/// <summary>
/// A typed, persisted edge in a board's task graph — the single source of truth for a
/// connection (topology) and its semantics (kind/label/loop config). Id is "{source}->{target}".
/// </summary>
public class TaskEdge
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty; // "{sourceTaskId}->{targetTaskId}"

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("sourceTaskId")]
    public string SourceTaskId { get; set; } = string.Empty;

    [JsonPropertyName("targetTaskId")]
    public string TargetTaskId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public EdgeKind Kind { get; set; } = EdgeKind.Dependency;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("maxIterations")]
    public int MaxIterations { get; set; } = 3;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string MakeId(string source, string target) => $"{source}->{target}";
}
```

- [ ] **Step 2: Build** — `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj -c Release`. Expected: succeeds.
- [ ] **Step 3: Commit** — `git add src/core/TectikaAgents.Core/Models/TaskEdge.cs && git commit -m "feat(core): add TaskEdge model + EdgeKind"`

---

## Task 2: Cosmos edge container + data-access methods

**Files:** Modify `ICosmosDbService.cs`, `CosmosDbService.cs`, `InMemoryCosmosDbService.cs`

- [ ] **Step 1: Add interface methods** (`src/api/.../Services/ICosmosDbService.cs`, in a new `// ── Edges ──` section)

```csharp
// ── Edges ──────────────────────────────────────────────────────────────────
Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default);
Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default);
Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default);
Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default);
Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default);
Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `CosmosDbService.cs`** — add the container constant next to the others, register it in `EnsureInfrastructureAsync`, and add the methods (follow the existing `GetContainer`/`QueryAsync`/`ReplaceItemAsync`/NotFound→null patterns already in this file).

```csharp
public const string TaskEdgesContainer = "taskEdges";
// ...in EnsureInfrastructureAsync containers array, add:  (TaskEdgesContainer, "/boardId"),

public async Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
{
    var res = await GetContainer(TaskEdgesContainer)
        .CreateItemAsync(edge, new PartitionKey(edge.BoardId), cancellationToken: ct);
    return res.Resource;
}

public async Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default)
{
    var q = new QueryDefinition("SELECT * FROM c WHERE c.boardId = @b").WithParameter("@b", boardId);
    return await QueryAsync<TaskEdge>(TaskEdgesContainer, q, boardId, ct);
}

public async Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
{
    try
    {
        var res = await GetContainer(TaskEdgesContainer)
            .ReadItemAsync<TaskEdge>(edgeId, new PartitionKey(boardId), cancellationToken: ct);
        return res.Resource;
    }
    catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
}

public async Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
{
    edge.UpdatedAt = DateTimeOffset.UtcNow;
    var res = await GetContainer(TaskEdgesContainer)
        .ReplaceItemAsync(edge, edge.Id, new PartitionKey(edge.BoardId), cancellationToken: ct);
    return res.Resource;
}

public async Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
{
    try { await GetContainer(TaskEdgesContainer).DeleteItemAsync<TaskEdge>(edgeId, new PartitionKey(boardId), cancellationToken: ct); }
    catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
}

public async Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default)
{
    var q = new QueryDefinition("SELECT * FROM c WHERE c.boardId=@b AND (c.sourceTaskId=@t OR c.targetTaskId=@t)")
        .WithParameter("@b", boardId).WithParameter("@t", taskId);
    foreach (var e in await QueryAsync<TaskEdge>(TaskEdgesContainer, q, boardId, ct))
        await DeleteEdgeAsync(boardId, e.Id, ct);
}
```

> Match the exact signatures/helpers already present (`GetContainer`, `QueryAsync<T>(container, query, partitionKey, ct)`). If `QueryAsync` has a different arity in this file, adapt the calls to it — do not invent a new helper.

- [ ] **Step 3: Implement in `InMemoryCosmosDbService.cs`** — add a store + methods mirroring the others.

```csharp
private readonly ConcurrentDictionary<string, TaskEdge> _edges = new();

public Task<TaskEdge> CreateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
{ _edges[edge.Id] = edge; return Task.FromResult(edge); }

public Task<IEnumerable<TaskEdge>> GetEdgesByBoardAsync(string boardId, CancellationToken ct = default)
=> Task.FromResult(_edges.Values.Where(e => e.BoardId == boardId).AsEnumerable());

public Task<TaskEdge?> GetEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
=> Task.FromResult(_edges.TryGetValue(edgeId, out var e) && e.BoardId == boardId ? e : null);

public Task<TaskEdge> UpdateEdgeAsync(TaskEdge edge, CancellationToken ct = default)
{ edge.UpdatedAt = DateTimeOffset.UtcNow; _edges[edge.Id] = edge; return Task.FromResult(edge); }

public Task DeleteEdgeAsync(string boardId, string edgeId, CancellationToken ct = default)
{ _edges.TryRemove(edgeId, out _); return Task.CompletedTask; }

public Task DeleteEdgesForTaskAsync(string boardId, string taskId, CancellationToken ct = default)
{ foreach (var e in _edges.Values.Where(e => e.BoardId == boardId && (e.SourceTaskId == taskId || e.TargetTaskId == taskId)).ToList()) _edges.TryRemove(e.Id, out _); return Task.CompletedTask; }
```

- [ ] **Step 4: Build** — `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj -c Release`. Expected: succeeds (no missing-member errors → both impls satisfy the interface).
- [ ] **Step 5: Commit** — `git commit -am "feat(api): taskEdges container + ICosmosDbService edge methods"`

---

## Task 3: Kind auto-detection (pure helper)

**Files:** Create `src/api/TectikaAgents.Api/Services/EdgeKindDetector.cs`

- [ ] **Step 1: Implement** — given existing edges + a proposed (source→target), return `QaFeedback` if `target` can already reach `source` over **Dependency edges only**, else `Dependency`.

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public static class EdgeKindDetector
{
    /// <summary>QaFeedback if adding source→target closes a cycle (target already reaches source
    /// via Dependency edges); otherwise Dependency.</summary>
    public static EdgeKind Detect(IEnumerable<TaskEdge> existing, string sourceTaskId, string targetTaskId)
    {
        var adj = new Dictionary<string, List<string>>();
        foreach (var e in existing.Where(e => e.Kind == EdgeKind.Dependency))
        {
            if (!adj.TryGetValue(e.SourceTaskId, out var l)) adj[e.SourceTaskId] = l = new();
            l.Add(e.TargetTaskId);
        }
        // BFS from target; if we reach source, the new edge closes a loop.
        var seen = new HashSet<string> { targetTaskId };
        var queue = new Queue<string>(); queue.Enqueue(targetTaskId);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            if (u == sourceTaskId) return EdgeKind.QaFeedback;
            if (adj.TryGetValue(u, out var outs))
                foreach (var v in outs) if (seen.Add(v)) queue.Enqueue(v);
        }
        return EdgeKind.Dependency;
    }
}
```

- [ ] **Step 2: Build** — `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj -c Release`. Expected: succeeds.
- [ ] **Step 3: Commit** — `git commit -am "feat(api): EdgeKindDetector (server-side back-edge detection)"`

---

## Task 4: EdgesController + remove /connect + cascade

**Files:** Create `src/api/.../Controllers/EdgesController.cs`; modify `TasksController.cs`

- [ ] **Step 1: Create EdgesController** (follow `TasksController`'s base-class/route/`TenantId` conventions — open it first to copy the `[Authorize]`, route prefix, and `TenantId` property exactly).

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/boards/{boardId}/edges")]
public class EdgesController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    public EdgesController(ICosmosDbService cosmos) => _cosmos = cosmos;

    // Copy TenantId resolution from TasksController (e.g. User.FindFirst("tid")?.Value ?? "default").
    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    [HttpGet]
    public async Task<IActionResult> List(string boardId, CancellationToken ct)
        => Ok(await _cosmos.GetEdgesByBoardAsync(boardId, ct));

    public record CreateEdgeRequest(string SourceTaskId, string TargetTaskId, EdgeKind? Kind, string? Label);

    [HttpPost]
    public async Task<IActionResult> Create(string boardId, [FromBody] CreateEdgeRequest req, CancellationToken ct)
    {
        if (req.SourceTaskId == req.TargetTaskId) return BadRequest("A task cannot link to itself.");
        var src = await _cosmos.GetTaskAsync(boardId, req.SourceTaskId, ct);
        var dst = await _cosmos.GetTaskAsync(boardId, req.TargetTaskId, ct);
        if (src is null || dst is null) return NotFound("Source or target task not found.");

        var id = TaskEdge.MakeId(req.SourceTaskId, req.TargetTaskId);
        if (await _cosmos.GetEdgeAsync(boardId, id, ct) is not null) return Conflict("Edge already exists.");

        var existing = await _cosmos.GetEdgesByBoardAsync(boardId, ct);
        var kind = req.Kind ?? EdgeKindDetector.Detect(existing, req.SourceTaskId, req.TargetTaskId);
        var edge = new TaskEdge
        {
            Id = id, TenantId = TenantId, BoardId = boardId,
            SourceTaskId = req.SourceTaskId, TargetTaskId = req.TargetTaskId,
            Kind = kind, Label = req.Label,
            // MaxIterations defaults to 3 on the model; only consumed by Phase 7's QA loops.
        };
        return Ok(await _cosmos.CreateEdgeAsync(edge, ct));
    }

    public record UpdateEdgeRequest(EdgeKind? Kind, string? Label, string? Condition, int? MaxIterations);

    [HttpPut("{edgeId}")]
    public async Task<IActionResult> Update(string boardId, string edgeId, [FromBody] UpdateEdgeRequest req, CancellationToken ct)
    {
        var edge = await _cosmos.GetEdgeAsync(boardId, edgeId, ct);
        if (edge is null) return NotFound();
        if (req.Kind is not null) edge.Kind = req.Kind.Value;
        if (req.Label is not null) edge.Label = req.Label.Length == 0 ? null : req.Label;
        if (req.Condition is not null) edge.Condition = req.Condition.Length == 0 ? null : req.Condition;
        if (req.MaxIterations is not null) edge.MaxIterations = req.MaxIterations.Value;
        return Ok(await _cosmos.UpdateEdgeAsync(edge, ct));
    }

    [HttpDelete("{edgeId}")]
    public async Task<IActionResult> Delete(string boardId, string edgeId, CancellationToken ct)
    { await _cosmos.DeleteEdgeAsync(boardId, edgeId, ct); return NoContent(); }
}
```

> Verify the `TenantId` expression matches `TasksController`'s exactly; if that controller uses a shared base class, derive from it instead of re-implementing.

- [ ] **Step 2: TasksController — remove `/connect` and the array patch.** Delete the `ConnectTasks` action + `ConnectTaskRequest` record; in `Update`, delete the two lines patching `UpstreamTaskIds`/`DownstreamTaskIds`; remove those two props from `UpdateTaskRequest`. In the task `Delete` action add, before/after removing the task: `await _cosmos.DeleteEdgesForTaskAsync(boardId, taskId, ct);`
- [ ] **Step 3: Build** — `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj -c Release`. Expected: succeeds. (It will still reference `AgentTask` arrays until Task 5; that's fine — they still exist now.)
- [ ] **Step 4: Behavioral check (mock mode).** Start the API in mock mode (see header). Then:
```bash
curl -s -XPOST localhost:5000/api/boards/board-001/edges -H 'Content-Type: application/json' \
  -d '{"sourceTaskId":"task-spec","targetTaskId":"task-impl"}' | python3 -m json.tool   # kind=Dependency
# create a loop: task-deploy already downstream of spec→impl→review→deploy; link deploy->spec
curl -s -XPOST localhost:5000/api/boards/board-001/edges -H 'Content-Type: application/json' \
  -d '{"sourceTaskId":"task-deploy","targetTaskId":"task-spec"}' | python3 -c "import sys,json;print('kind=',json.load(sys.stdin)['kind'])"  # expect QaFeedback once Task 7 seeds dep edges
curl -s localhost:5000/api/boards/board-001/edges | python3 -c "import sys,json;print(len(json.load(sys.stdin)),'edges')"
```
Expected: first create returns `kind=Dependency`; list returns the created edges. (Full QaFeedback assertion is meaningful after Task 7 seeds the dependency chain; until then the mock board has no dependency edges so the loop check yields Dependency — re-verify after Task 7.)
- [ ] **Step 5: Commit** — `git commit -am "feat(api): EdgesController CRUD; remove /connect + array patch; cascade edge delete"`

---

## Task 5: Remove the arrays from AgentTask

**Files:** Modify `src/core/.../Models/AgentTask.cs`

- [ ] **Step 1:** Delete the `UpstreamTaskIds` and `DownstreamTaskIds` properties (keep `Dependencies` untouched).
- [ ] **Step 2: Build the whole solution** — `dotnet build TectikaAgents.slnx -c Release`. Expected: **compile errors** in every remaining consumer (workflows, seed). That list is your migration checklist for Tasks 6–7. Do not "fix" by re-adding the fields.
- [ ] **Step 3: Commit** after Tasks 6–7 make it build (do not commit a broken build). Leave this task's change uncommitted until 6–7 are done, then: `git commit -am "refactor(core): remove upstream/downstream arrays from AgentTask (edges are source of truth)"`

---

## Task 6: Workflow consumers read edges

**Files:** Modify `src/workflows/Services/WorkflowCosmosService.cs`, `Activities/InvokeAgentActivity.cs`

- [ ] **Step 1: Add an edges query to `WorkflowCosmosService`** (it already builds Cosmos container refs via a `C("name")` helper — reuse it).

```csharp
public async Task<List<string>> GetUpstreamTaskIdsAsync(string boardId, string taskId, CancellationToken ct = default)
{
    var ids = new List<string>();
    var q = new QueryDefinition(
        "SELECT VALUE c.sourceTaskId FROM c WHERE c.boardId=@b AND c.targetTaskId=@t AND c.kind='Dependency'")
        .WithParameter("@b", boardId).WithParameter("@t", taskId);
    var iter = C("taskEdges").GetItemQueryIterator<string>(q,
        requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId) });
    while (iter.HasMoreResults) ids.AddRange(await iter.ReadNextAsync(ct));
    return ids;
}
```

> `kind='Dependency'` matches the System.Text.Json string-enum storage (the API serializes enums as names — confirmed by the Cosmos serializer config). If stored values differ, align the literal.

- [ ] **Step 2: Update `InvokeAgentActivity`** — replace the line that read `task.UpstreamTaskIds` with:
```csharp
var upstreamTaskIds = await _cosmos.GetUpstreamTaskIdsAsync(task.BoardId, input.TaskId, ct);
var upstreamArtifacts = await _cosmos.GetUpstreamArtifactsAsync(upstreamTaskIds, ct);
```
(Keep the existing `GetUpstreamArtifactsAsync(IEnumerable<string>)` as-is.)
- [ ] **Step 3: Build workflows** — `dotnet build src/workflows/TectikaAgents.Workflows.csproj -c Release`. Expected: succeeds.
- [ ] **Step 4: Commit** — `git commit -am "refactor(workflows): derive upstream task ids from Dependency edges"`

---

## Task 7: Seed emits edges

**Files:** Modify `src/api/.../Services/MockData/MockDataSeeder.cs`, `CosmosDataSeeder.cs`

- [ ] **Step 1: MockDataSeeder** — change the `Seed(...)` signature to also take `ConcurrentDictionary<string, TaskEdge> edges`; remove every `UpstreamTaskIds`/`DownstreamTaskIds` assignment on the task objects; for each former dependency (e.g. `task-spec→task-impl`, `task-impl→task-review`, `task-review→task-deploy`, plus the equivalents in boards 002/003) add:
```csharp
void AddEdge(string s, string t, EdgeKind k = EdgeKind.Dependency, string? label = null) =>
    edges[TaskEdge.MakeId(s, t)] = new TaskEdge {
        Id = TaskEdge.MakeId(s, t), TenantId = Tenant, BoardId = /* the task's board */ BoardId,
        SourceTaskId = s, TargetTaskId = t, Kind = k, Label = label,
        CreatedAt = now.AddDays(-9), UpdatedAt = now.AddDays(-9) };
AddEdge(TaskSpec, TaskImpl); AddEdge(TaskImpl, TaskReview); AddEdge(TaskReview, TaskDeploy);
// ...replicate the dependency pairs that previously lived in the Upstream/Downstream arrays for every board.
```
> Recreate exactly the edges the arrays used to express (read the pre-change arrays from git: `git show HEAD~:src/api/.../MockData/MockDataSeeder.cs`). Set `BoardId` per the task's board, not a single constant, for the boards that have multiple.
- [ ] **Step 2: Update both callers of `MockDataSeeder.Seed`** to pass an edges dictionary:
  - `InMemoryCosmosDbService` ctor — add `_edges` and pass it.
  - `CosmosDataSeeder.SeedAsync` — add a local `edges` dict, pass it, then after writing tasks: `foreach (var e in edges.Values) await cosmos.CreateEdgeAsync(e, ct);`
- [ ] **Step 3: Build** — `dotnet build TectikaAgents.slnx -c Release`. Expected: succeeds (this is also where Task 5's removal finally compiles end-to-end).
- [ ] **Step 4: Behavioral check (mock mode)** — restart the API in mock mode:
```bash
curl -s localhost:5000/api/boards/board-001/edges | python3 -c "import sys,json;d=json.load(sys.stdin);print(len(d),'edges');[print(e['sourceTaskId'],'->',e['targetTaskId'],e['kind']) for e in d]"
# now re-run the loop check from Task 4 step 4 — deploy->spec must come back kind=QaFeedback
```
Expected: the demo dependency edges exist with `kind=Dependency`; a deploy→spec edge auto-detects `QaFeedback`.
- [ ] **Step 5: Commit** both Task 5 and Task 7 — `git commit -am "feat(api): seed TaskEdge docs; drop array seeding (Task 5 + 7)"`

---

## Task 8: One-time backfill (arrays → edges)

**Files:** Create `src/api/.../Services/MockData/EdgeBackfill.cs`; modify `Program.cs`

- [ ] **Step 1: Implement the backfill** — raw-read existing task docs (the stored `downstreamTaskIds` survive on old docs even though the model dropped them) and create edges idempotently.

```csharp
using Microsoft.Azure.Cosmos;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services.MockData;

internal static class EdgeBackfill
{
    public static async Task RunAsync(CosmosClient client, string dbName, ICosmosDbService cosmos, ILogger log, CancellationToken ct = default)
    {
        var tasks = client.GetContainer(dbName, CosmosDbService.TasksContainer);
        var q = new QueryDefinition("SELECT c.id, c.boardId, c.tenantId, c.downstreamTaskIds FROM c");
        var iter = tasks.GetItemQueryIterator<RawTask>(q);
        int made = 0;
        while (iter.HasMoreResults)
            foreach (var t in await iter.ReadNextAsync(ct))
                foreach (var dst in t.DownstreamTaskIds ?? new())
                {
                    var id = TaskEdge.MakeId(t.Id, dst);
                    if (await cosmos.GetEdgeAsync(t.BoardId, id, ct) is not null) continue;
                    var existing = await cosmos.GetEdgesByBoardAsync(t.BoardId, ct);
                    var kind = EdgeKindDetector.Detect(existing, t.Id, dst);
                    await cosmos.CreateEdgeAsync(new TaskEdge {
                        Id = id, TenantId = t.TenantId, BoardId = t.BoardId,
                        SourceTaskId = t.Id, TargetTaskId = dst, Kind = kind }, ct);
                    made++;
                }
        log.LogInformation("Edge backfill complete — {Made} edges created.", made);
    }
    private class RawTask { public string Id {get;set;}="" ; public string BoardId {get;set;}=""; public string TenantId {get;set;}="default"; public List<string>? DownstreamTaskIds {get;set;} }
}
```

- [ ] **Step 2: Wire a `--backfill-edges` mode in `Program.cs`** (next to the `--seed-only` block, real-DB only):
```csharp
if (!useMockDatabase && args.Contains("--backfill-edges"))
{
    using var scope = app.Services.CreateScope();
    var cosmos = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
    var client = scope.ServiceProvider.GetRequiredService<CosmosClient>();
    var dbName = builder.Configuration["CosmosDb:DatabaseName"] ?? "tectikaagents";
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("EdgeBackfill");
    await TectikaAgents.Api.Services.MockData.EdgeBackfill.RunAsync(client, dbName, cosmos, log);
    return;
}
```
- [ ] **Step 3: Build** — `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj -c Release`. Expected: succeeds.
- [ ] **Step 4: Commit** — `git commit -am "feat(api): --backfill-edges one-time arrays->TaskEdge migration"`

---

## Task 9: Frontend types + API client

**Files:** Modify `src/web/tectika-board/src/lib/types.ts`, `api.ts`

- [ ] **Step 1: types.ts** — add and remove:
```typescript
export type EdgeKind = 'Dependency' | 'QaFeedback';
export interface TaskEdge {
  id: string; tenantId: string; boardId: string;
  sourceTaskId: string; targetTaskId: string;
  kind: EdgeKind; label?: string; condition?: string; maxIterations: number;
  createdAt: string; updatedAt: string;
}
```
Remove `upstreamTaskIds` and `downstreamTaskIds` from `AgentTask`; remove them from `TaskPatch`.
- [ ] **Step 2: api.ts** — remove `tasks.connect`; add:
```typescript
edges: {
  list: (boardId: string) => fetchApi<TaskEdge[]>(`/api/boards/${boardId}/edges`),
  create: (boardId: string, body: { sourceTaskId: string; targetTaskId: string; kind?: EdgeKind; label?: string }) =>
    fetchApi<TaskEdge>(`/api/boards/${boardId}/edges`, { method: 'POST', body: JSON.stringify(body) }),
  update: (boardId: string, edgeId: string, patch: Partial<Pick<TaskEdge,'kind'|'label'|'condition'|'maxIterations'>>) =>
    fetchApi<TaskEdge>(`/api/boards/${boardId}/edges/${encodeURIComponent(edgeId)}`, { method: 'PUT', body: JSON.stringify(patch) }),
  remove: (boardId: string, edgeId: string) =>
    fetchApi<void>(`/api/boards/${boardId}/edges/${encodeURIComponent(edgeId)}`, { method: 'DELETE' }),
},
```
- [ ] **Step 3: Type-check** — `cd src/web/tectika-board && npx tsc --noEmit`. Expected: errors only in `board-context.tsx`/`CanvasView.tsx` consumers (fixed in Tasks 10–11).
- [ ] **Step 4: Commit** — `git commit -am "feat(web): TaskEdge type + edges API client; drop task arrays + connect"`

---

## Task 10: board-context — edges state, no localStorage labels

**Files:** Modify `src/web/tectika-board/src/lib/board-context.tsx`

- [ ] **Step 1:** Add `edges: TaskEdge[]` state + `setEdges`. Fetch via `api.edges.list(boardId)` in the same effect that loads tasks, and reconcile in the existing **7s poll** (fetch `api.edges.list` alongside `api.tasks.list`, replace `edges` state).
- [ ] **Step 2:** Remove `edgeLabels` from `BoardConfig`, `defaultConfig()`, and all localStorage read/write of it. Rewrite the edge ops:
```typescript
const connectEdge = useCallback(async (source: string, target: string) => {
  try { const e = await api.edges.create(boardId, { sourceTaskId: source, targetTaskId: target }); setEdges(p => [...p.filter(x=>x.id!==e.id), e]); }
  catch { toast('Could not connect items', 'error'); }
}, [boardId]);
const disconnectEdge = useCallback(async (edgeId: string) => {
  setEdges(p => p.filter(e => e.id !== edgeId));
  try { await api.edges.remove(boardId, edgeId); } catch { toast('Could not remove connection', 'error'); }
}, [boardId]);
const updateEdge = useCallback(async (edgeId: string, patch: Partial<Pick<TaskEdge,'kind'|'label'>>) => {
  setEdges(p => p.map(e => e.id === edgeId ? { ...e, ...patch } : e));
  try { await api.edges.update(boardId, edgeId, patch); } catch { toast('Could not update edge', 'error'); }
}, [boardId]);
```
Expose `edges`, `connectEdge`, `disconnectEdge`, `updateEdge` on the context value; drop `connectTasks`/`disconnectTasks`/`setEdgeLabel`.
- [ ] **Step 3: Type-check** — `npx tsc --noEmit` (errors now only in `CanvasView.tsx`).
- [ ] **Step 4: Commit** — `git commit -am "feat(web): edges in board-context (server-backed, polled); remove localStorage edgeLabels"`

---

## Task 11: CanvasView — build from edges, drop the DFS

**Files:** Modify `src/web/tectika-board/src/components/board/canvas/CanvasView.tsx`

- [ ] **Step 1:** Build display edges from context `edges` (map each `TaskEdge`→xyflow `Edge` with `id`, `source=sourceTaskId`, `target=targetTaskId`, `data:{ feedback: e.kind==='QaFeedback', label: e.label ?? '' }`, marker by kind). **Delete `feedbackEdgeIds` and its usage** — kind comes from the edge now.
- [ ] **Step 2:** Rewire handlers to context edge ops: `onConnect → connectEdge(source,target)` (use the returned edge's kind for styling — context already stores it; show the loop toast when `data.feedback`); `onEdgesDelete → disconnectEdge(`${e.source}->${e.target}`)`; `onReconnect → disconnectEdge(old) + connectEdge(new)` carrying the label via a follow-up `updateEdge`; label edit (`EdgeLabelInput`) → `updateEdge(id,{label})`.
- [ ] **Step 3:** Add the minimal **kind override** — in the existing edge label popover add a "Feedback loop" checkbox bound to `data.feedback` → `updateEdge(id,{ kind: checked ? 'QaFeedback' : 'Dependency' })`.
- [ ] **Step 4: Build + lint** — `npm run build && npm run lint`. Expected: succeeds.
- [ ] **Step 5: Commit** — `git commit -am "feat(web): canvas reads typed edges; kind from server; override toggle"`

---

## Task 12: Consumer audit (other readers of the arrays)

**Files:** repo-wide search

- [ ] **Step 1:** `grep -rn "upstreamTaskIds\|downstreamTaskIds" src/web src/api src/workflows src/core` — every remaining hit is a consumer to migrate (e.g. a dependency column type, a timeline/table view). For each, derive the relationship from `edges` instead. If a board view shows "depends on", compute it from `edges.filter(e=>e.targetTaskId===task.id && e.kind==='Dependency')`.
- [ ] **Step 2: Build + lint both stacks** — `dotnet build TectikaAgents.slnx -c Release` and `cd src/web/tectika-board && npm run build`. Expected: both succeed, zero references to the removed arrays remain.
- [ ] **Step 3: Commit** — `git commit -am "refactor: migrate remaining upstream/downstream consumers to edges"`

---

## Task 13: Provision live Cosmos + backfill + verify

**Files:** none (ops)

- [ ] **Step 1: Create the live container** —
```bash
az cosmosdb sql container create -a cosmos-agentteam -g rg-agentteam-dev-001 -d tectikaagents -n taskEdges -p /boardId
```
- [ ] **Step 2: Backfill existing data** (uses az-login creds / DefaultAzureCredential) —
```bash
cd src/api/TectikaAgents.Api
dotnet run -c Release -- --backfill-edges      # MockDatabase__Enabled=false from appsettings; real Cosmos
```
Expected log: `Edge backfill complete — N edges created.`
- [ ] **Step 3: Verify in mock + against the deployed flow** — start the API (mock or real) and confirm `GET /api/boards/board-001/edges` returns the dependency chain; in the browser, draw an edge, **hard-refresh**, confirm it persists with kind+label; open a second browser and confirm it appears within ~7s; run the existing Playwright canvas check (`node src/web/tectika-board/qa/qa2.mjs` style) and confirm ≥3 dependency edges still render.
- [ ] **Step 4: Commit** any verification notes; open the PR for `feat/phase0-typed-edges`.

---

## Self-review notes (coverage)

- Spec §data-model → Tasks 1, 5, 9. §storage/data-access → Task 2. §API + kind detect → Tasks 3–4. §workflow consumers → Task 6. §frontend → Tasks 9–12. §migration (seed + backfill) → Tasks 7–8, 13. §testing → per-task mock-mode curl + build + Task 13 browser/Playwright checks.
- `EdgeKind` is serialized as the string name on both sides (`'Dependency'`/`'QaFeedback'`) — the workflow SQL literal and the TS union must match these exact names.
