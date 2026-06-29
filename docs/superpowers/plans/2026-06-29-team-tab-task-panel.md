# Team Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a 5th "Team" tab to the task `ItemPanel` — a human↔human space with a durable **Notes** zone (typed, editable; per-note "share with agent") and a flat **Discussion** feed (@-mentions, reactions, edit/delete, unread badge), backed by a new `taskComments` Cosmos container and a `CommentsController`, with 4s polling for live updates and an agent-side `read_team_notes` tool.

**Architecture:** New `taskComments` container (partition `/taskId`) holds one document type with a `kind` string discriminator (`note`|`message`). A `CommentsController` provides CRUD + reactions + share-toggle + mark-read, scoped by the `tid`/`preferred_username` claims. The frontend `TeamTab` fetches its own data (like the other tabs), polls every 4s, and pushes all logic into pure helpers tested with `node:test`. @-mentions reuse the existing notifications system (with a small per-recipient addition). The agent reads shared notes on demand via a new board-scoped `read_team_notes` tool.

**Tech Stack:** .NET 10 / ASP.NET Core controllers + Cosmos SDK (backend), xUnit (backend tests), Next.js 16 / React 19 / Tailwind v4 (frontend), `node:test` (frontend tests), Bicep (infra).

**Spec:** [docs/superpowers/specs/2026-06-29-team-tab-task-panel-design.md](../specs/2026-06-29-team-tab-task-panel-design.md)

**Conventions (verified in this codebase):**
- Backend models: `[JsonPropertyName("camelCase")]`, `id` = GUID string, `DateTimeOffset` UTC timestamps. String discriminators over enums (precedent: `NotificationDocument.Type`).
- Cosmos access: `ICosmosDbService` + its in-memory twin `InMemoryCosmosDbService` (both must implement new methods). Local dev runs in mock mode as `eli@tectika.com`.
- Backend tests: xUnit in `tests/TectikaAgents.Tests/`; inline stubs or `InMemoryCosmosDbService`; `NullLogger<T>.Instance`. Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`.
- Frontend tests: `node:test` + `node:assert/strict`, files `*.test.ts` next to source or under `__tests__/`. Run: `npm test --prefix src/web/tectika-board`.
- **Deployment idempotency rule:** any new container goes in BOTH `CosmosDbService.ContainerDefinitions` AND `infra/modules/data.bicep`, and must be created in prod explicitly (`EnsureInfrastructureAsync` swallows failures).

**Phase map:**
- **Phase 1 — Backend data layer:** model + container wiring + `ICosmosDbService`/`InMemoryCosmosDbService` methods.
- **Phase 2 — Backend API:** `CommentsController` (CRUD, reactions, share, mark-read) + `UserSettingsDocument.TaskReadMarkers`.
- **Phase 3 — Backend mentions:** `NotificationDocument.RecipientUserId` + mention notifications.
- **Phase 4 — Frontend types + API client.**
- **Phase 5 — Frontend shared markdown + mention rendering.**
- **Phase 6 — Frontend TeamTab (helpers + component + wiring).**
- **Phase 7 — Agent `read_team_notes` tool** (independently shippable, last).
- **Phase 8 — Deploy + verify.**

---

## Phase 1 — Backend data layer

### Task 1: `TaskComment` model

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/TaskComment.cs`

- [ ] **Step 1: Create the model**

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// A human↔human comment on a task. Two kinds share one document:
/// "note" (durable, typed, editable, shareable with the agent) and
/// "message" (flat discussion feed). Stored in the taskComments container,
/// partitioned by /taskId. kind/noteType are string discriminators (matching
/// NotificationDocument.Type) to avoid enum-casing drift with the frontend.
/// </summary>
public class TaskComment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;       // partition key

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>"note" | "message"</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    /// <summary>"decision" | "open_question" | "note" — notes only</summary>
    [JsonPropertyName("noteType")]
    public string? NoteType { get; set; }

    [JsonPropertyName("authorId")]
    public string AuthorId { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string> Mentions { get; set; } = [];

    /// <summary>emoji -> userIds</summary>
    [JsonPropertyName("reactions")]
    public Dictionary<string, List<string>> Reactions { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("editedBy")]
    public string? EditedBy { get; set; }

    [JsonPropertyName("deletedAt")]
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>D1: note is readable by the agent's read_team_notes tool (notes only).</summary>
    [JsonPropertyName("sharedWithAgent")]
    public bool SharedWithAgent { get; set; }

    [JsonPropertyName("sharedAt")]
    public DateTimeOffset? SharedAt { get; set; }

    [JsonPropertyName("sharedBy")]
    public string? SharedBy { get; set; }
}

/// <summary>Valid string discriminator values for TaskComment.</summary>
public static class CommentKinds
{
    public const string Note = "note";
    public const string Message = "message";
    public static readonly string[] All = [Note, Message];
}

public static class NoteTypes
{
    public const string Decision = "decision";
    public const string OpenQuestion = "open_question";
    public const string Note = "note";
    public static readonly string[] All = [Decision, OpenQuestion, Note];
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/TaskComment.cs
git commit -m "feat(core): TaskComment model for team notes + discussion"
```

---

### Task 2: Register the `taskComments` container (runtime + infra)

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs` (container consts ~line 18-32, `ContainerDefinitions` ~line 36-52)
- Modify: `infra/modules/data.bicep` (containers array ~line 34-51)

- [ ] **Step 1: Add the container const**

In `CosmosDbService.cs`, after the `PreviewSessionsContainer` const, add:

```csharp
    public const string TaskCommentsContainer = "taskComments";
```

- [ ] **Step 2: Add to `ContainerDefinitions`**

In the `ContainerDefinitions` array, after `(PreviewSessionsContainer, "/boardId"),` add:

```csharp
    (TaskCommentsContainer,      "/taskId"),
```

- [ ] **Step 3: Add to Bicep (idempotency rule)**

In `infra/modules/data.bicep`, in the `containers` var array, after `{ name: 'previewSessions', pk: '/boardId' }` add:

```bicep
  { name: 'taskComments', pk: '/taskId' }
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/CosmosDbService.cs infra/modules/data.bicep
git commit -m "feat(api): register taskComments container (runtime + bicep)"
```

---

### Task 3: `ICosmosDbService` comment methods + Cosmos impl + in-memory impl

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs`
- Test: `tests/TectikaAgents.Tests/TaskCommentsStoreTests.cs` (create)

- [ ] **Step 1: Write the failing test (against the in-memory impl)**

Create `tests/TectikaAgents.Tests/TaskCommentsStoreTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class TaskCommentsStoreTests
{
    private static InMemoryCosmosDbService NewStore() =>
        new(NullLogger<InMemoryCosmosDbService>.Instance);

    [Fact]
    public async Task Create_then_GetByTask_returns_ordered_comments()
    {
        var store = NewStore();
        await store.CreateCommentAsync(new TaskComment
        {
            TaskId = "t1", BoardId = "b1", TenantId = "default",
            Kind = "message", AuthorId = "eli@tectika.com", Body = "first",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await store.CreateCommentAsync(new TaskComment
        {
            TaskId = "t1", BoardId = "b1", TenantId = "default",
            Kind = "message", AuthorId = "maya@tectika.com", Body = "second",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.CreateCommentAsync(new TaskComment
        {
            TaskId = "t2", BoardId = "b1", TenantId = "default",
            Kind = "message", AuthorId = "eli@tectika.com", Body = "other task"
        });

        var list = await store.GetCommentsByTaskAsync("t1");

        Assert.Equal(2, list.Count);
        Assert.Equal("first", list[0].Body);
        Assert.Equal("second", list[1].Body);
    }

    [Fact]
    public async Task GetComment_scoped_to_task_partition()
    {
        var store = NewStore();
        var c = await store.CreateCommentAsync(new TaskComment { TaskId = "t1", Body = "x" });

        Assert.NotNull(await store.GetCommentAsync("t1", c.Id));
        Assert.Null(await store.GetCommentAsync("tWRONG", c.Id));
    }

    [Fact]
    public async Task Upsert_replaces_existing()
    {
        var store = NewStore();
        var c = await store.CreateCommentAsync(new TaskComment { TaskId = "t1", Body = "old" });
        c.Body = "new";
        await store.UpsertCommentAsync(c);

        var reloaded = await store.GetCommentAsync("t1", c.Id);
        Assert.Equal("new", reloaded!.Body);
    }
}
```

- [ ] **Step 2: Run to verify it fails (no method)**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter TaskCommentsStoreTests`
Expected: FAIL — `ICosmosDbService` does not contain `CreateCommentAsync` (compile error).

- [ ] **Step 3: Add interface methods**

In `ICosmosDbService.cs`, add a `// ── Task comments ──` section:

```csharp
    // ── Task comments ────────────────────────────────────────────────────────────
    Task<TaskComment> CreateCommentAsync(TaskComment comment, CancellationToken ct = default);
    Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string taskId, CancellationToken ct = default);
    Task<TaskComment?> GetCommentAsync(string taskId, string commentId, CancellationToken ct = default);
    Task<TaskComment> UpsertCommentAsync(TaskComment comment, CancellationToken ct = default);
```

- [ ] **Step 4: Implement in `CosmosDbService`**

Add to `CosmosDbService.cs` (follow the existing `GetContainer`/`QueryAsync` helpers):

```csharp
    public async Task<TaskComment> CreateCommentAsync(TaskComment comment, CancellationToken ct = default)
    {
        var res = await GetContainer(TaskCommentsContainer)
            .CreateItemAsync(comment, new PartitionKey(comment.TaskId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string taskId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.taskId = @taskId ORDER BY c.createdAt ASC")
            .WithParameter("@taskId", taskId);
        return (await QueryAsync<TaskComment>(TaskCommentsContainer, query, taskId, ct)).ToList();
    }

    public async Task<TaskComment?> GetCommentAsync(string taskId, string commentId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(TaskCommentsContainer)
                .ReadItemAsync<TaskComment>(commentId, new PartitionKey(taskId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<TaskComment> UpsertCommentAsync(TaskComment comment, CancellationToken ct = default)
    {
        var res = await GetContainer(TaskCommentsContainer)
            .UpsertItemAsync(comment, new PartitionKey(comment.TaskId), cancellationToken: ct);
        return res.Resource;
    }
```

- [ ] **Step 5: Implement in `InMemoryCosmosDbService`**

Add a backing store field next to the other `ConcurrentDictionary` fields:

```csharp
    private readonly ConcurrentDictionary<string, TaskComment> _comments = new();
```

Add the methods:

```csharp
    public Task<TaskComment> CreateCommentAsync(TaskComment comment, CancellationToken ct = default)
    {
        _comments[comment.Id] = comment;
        return Task.FromResult(comment);
    }

    public Task<IReadOnlyList<TaskComment>> GetCommentsByTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<TaskComment>)_comments.Values
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAt)
            .ToList());

    public Task<TaskComment?> GetCommentAsync(string taskId, string commentId, CancellationToken ct = default) =>
        Task.FromResult(_comments.TryGetValue(commentId, out var c) && c.TaskId == taskId ? c : null);

    public Task<TaskComment> UpsertCommentAsync(TaskComment comment, CancellationToken ct = default)
    {
        _comments[comment.Id] = comment;
        return Task.FromResult(comment);
    }
```

- [ ] **Step 6: Run the tests — verify pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter TaskCommentsStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/ICosmosDbService.cs src/api/TectikaAgents.Api/Services/CosmosDbService.cs src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs tests/TectikaAgents.Tests/TaskCommentsStoreTests.cs
git commit -m "feat(api): taskComments store methods + tests"
```

---

## Phase 2 — Backend API

### Task 4: `UserSettingsDocument.TaskReadMarkers`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/UserSettingsDocument.cs`

- [ ] **Step 1: Add the read-marker map**

In `UserSettingsDocument`, add:

```csharp
    /// <summary>Per-task "Team tab last read at" markers for the unread badge. taskId -> timestamp.</summary>
    [JsonPropertyName("taskReadMarkers")]
    public Dictionary<string, DateTimeOffset> TaskReadMarkers { get; set; } = [];
```

- [ ] **Step 2: Build**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/UserSettingsDocument.cs
git commit -m "feat(core): per-task Team-tab read markers on UserSettingsDocument"
```

---

### Task 5: `CommentsController` — list + create

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/CommentsController.cs`
- Create: `src/api/TectikaAgents.Api/Controllers/CommentRequests.cs`
- Test: `tests/TectikaAgents.Tests/CommentsControllerTests.cs` (create)

- [ ] **Step 1: Write request DTOs**

Create `src/api/TectikaAgents.Api/Controllers/CommentRequests.cs`:

```csharp
namespace TectikaAgents.Api.Controllers;

public record CreateCommentRequest(string Kind, string? NoteType, string Body, List<string>? Mentions);
public record UpdateCommentRequest(string Body, string? NoteType);
public record ReactionRequest(string Emoji);
public record ShareRequest(bool Shared);
```

- [ ] **Step 2: Write the failing test (list + create + validation + tenant scoping)**

Create `tests/TectikaAgents.Tests/CommentsControllerTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class CommentsControllerTests
{
    private static InMemoryCosmosDbService NewStore() => new(NullLogger<InMemoryCosmosDbService>.Instance);

    private static CommentsController NewController(ICosmosDbService cosmos, string user = "eli@tectika.com", string tenant = "default")
    {
        var ctrl = new CommentsController(cosmos,
            new TestUserSettingsRepo(), new TestNotificationRepo(),
            NullLogger<CommentsController>.Instance);
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", tenant),
            new Claim("preferred_username", user),
        }, "Test"));
        ctrl.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = claims } };
        return ctrl;
    }

    // A task must exist + belong to the tenant for comment access.
    private static async Task SeedTask(ICosmosDbService cosmos, string boardId, string taskId, string tenant = "default") =>
        await cosmos.CreateTaskAsync(new AgentTask { Id = taskId, BoardId = boardId, TenantId = tenant, Title = "T" });

    [Fact]
    public async Task Create_then_List_returns_the_comment()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos);

        var created = await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "  hello team  ", null), default);
        Assert.IsType<OkObjectResult>(created);

        var list = Assert.IsType<OkObjectResult>(await ctrl.List("b1", "t1", default));
        var comments = Assert.IsAssignableFrom<IReadOnlyList<TaskComment>>(list.Value);
        Assert.Single(comments);
        Assert.Equal("hello team", comments[0].Body);            // trimmed
        Assert.Equal("eli@tectika.com", comments[0].AuthorId);   // from claim
        Assert.Equal("default", comments[0].TenantId);
    }

    [Fact]
    public async Task Create_rejects_blank_body()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos);

        var res = await ctrl.Create("b1", "t1", new CreateCommentRequest("message", null, "   ", null), default);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task Create_rejects_invalid_kind()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos);

        var res = await ctrl.Create("b1", "t1", new CreateCommentRequest("banana", null, "x", null), default);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task List_returns_NotFound_for_cross_tenant_task()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1", tenant: "otherTenant");
        var ctrl = NewController(cosmos, tenant: "default");

        Assert.IsType<NotFoundObjectResult>(await ctrl.List("b1", "t1", default));
    }

    // Minimal repo test doubles (UserSettingsRepository / NotificationRepository have virtual members).
    private sealed class TestUserSettingsRepo : UserSettingsRepository
    {
        public TestUserSettingsRepo() : base(null!, NullLogger<UserSettingsRepository>.Instance) { }
        private readonly Dictionary<string, UserSettingsDocument> _docs = new();
        public override Task<UserSettingsDocument> GetOrCreateAsync(string userId, CancellationToken ct = default)
        {
            if (!_docs.TryGetValue(userId, out var d)) { d = new UserSettingsDocument { UserId = userId }; _docs[userId] = d; }
            return Task.FromResult(d);
        }
        public override Task UpsertAsync(UserSettingsDocument doc, CancellationToken ct = default)
        { _docs[doc.UserId] = doc; return Task.CompletedTask; }
    }

    private sealed class TestNotificationRepo : NotificationRepository
    {
        public TestNotificationRepo() : base(null!, NullLogger<NotificationRepository>.Instance) { }
        public readonly List<NotificationDocument> Saved = new();
        public override Task SaveAsync(NotificationDocument doc, CancellationToken ct = default)
        { Saved.Add(doc); return Task.CompletedTask; }
    }
}
```

> **NOTE for implementer:** Confirm `UserSettingsRepository` and `NotificationRepository` expose a constructor and `virtual` methods usable by the test doubles above. The backend exploration confirmed `GetOrCreateAsync`/`UpsertAsync`/`SaveAsync` are `virtual`. If the base constructor signature differs (e.g. takes a `CosmosClient` or `Container`), pass `null!` only if the overridden methods never touch it — they don't here. If the base ctor eagerly dereferences its args, introduce a small `ICommentNotifier`/`IReadMarkerStore` seam instead and inject that. Decide at implementation time; the controller's public method signatures below are the contract the tests rely on.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: FAIL — `CommentsController` does not exist.

- [ ] **Step 4: Implement the controller (list + create only for this task)**

Create `src/api/TectikaAgents.Api/Controllers/CommentsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/tasks/{taskId}/comments")]
[Authorize]
public class CommentsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly UserSettingsRepository _userSettings;
    private readonly NotificationRepository _notifications;
    private readonly ILogger<CommentsController> _logger;

    public CommentsController(
        ICosmosDbService cosmos,
        UserSettingsRepository userSettings,
        NotificationRepository notifications,
        ILogger<CommentsController> logger)
    {
        _cosmos = cosmos;
        _userSettings = userSettings;
        _notifications = notifications;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    /// <summary>Loads the task only if it exists AND belongs to the caller's tenant.</summary>
    private async Task<AgentTask?> AuthorizedTaskAsync(string boardId, string taskId, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        return task is not null && task.TenantId == TenantId ? task : null;
    }

    [HttpGet]
    public async Task<IActionResult> List(string boardId, string taskId, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null)
            return NotFound("Task not found.");
        return Ok(await _cosmos.GetCommentsByTaskAsync(taskId, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string boardId, string taskId, [FromBody] CreateCommentRequest req, CancellationToken ct)
    {
        var task = await AuthorizedTaskAsync(boardId, taskId, ct);
        if (task is null) return NotFound("Task not found.");

        var body = (req.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest("Comment body is required.");
        if (!CommentKinds.All.Contains(req.Kind)) return BadRequest("Invalid kind.");
        if (req.Kind == CommentKinds.Note && req.NoteType is not null && !NoteTypes.All.Contains(req.NoteType))
            return BadRequest("Invalid noteType.");

        var comment = new TaskComment
        {
            TaskId = taskId,
            BoardId = boardId,
            TenantId = TenantId,
            Kind = req.Kind,
            NoteType = req.Kind == CommentKinds.Note ? (req.NoteType ?? NoteTypes.Note) : null,
            AuthorId = UserId,
            Body = body,
            Mentions = (req.Mentions ?? []).Distinct().ToList(),
        };

        var created = await _cosmos.CreateCommentAsync(comment, ct);
        await NotifyMentionsAsync(created, ct);   // implemented in Phase 3 (no-op stub for now)
        return Ok(created);
    }

    // Replaced with real implementation in Task 9.
    private Task NotifyMentionsAsync(TaskComment comment, CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 5: Run tests — verify pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/CommentsController.cs src/api/TectikaAgents.Api/Controllers/CommentRequests.cs tests/TectikaAgents.Tests/CommentsControllerTests.cs
git commit -m "feat(api): CommentsController list + create with validation + tenant scoping"
```

---

### Task 6: Edit + soft-delete (author-only)

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/CommentsController.cs`
- Modify: `tests/TectikaAgents.Tests/CommentsControllerTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `CommentsControllerTests`:

```csharp
    [Fact]
    public async Task Update_by_author_edits_body_and_stamps_editedBy()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var created = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "v1", null), default)).Value!;

        var res = await ctrl.Update("b1", "t1", created.Id, new UpdateCommentRequest("v2", null), default);
        var updated = (TaskComment)Assert.IsType<OkObjectResult>(res).Value!;
        Assert.Equal("v2", updated.Body);
        Assert.Equal("eli@tectika.com", updated.EditedBy);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task Update_by_non_author_is_forbidden()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var author = NewController(cosmos, user: "eli@tectika.com");
        var created = (TaskComment)((OkObjectResult)await author.Create("b1", "t1",
            new CreateCommentRequest("message", null, "v1", null), default)).Value!;

        var other = NewController(cosmos, user: "maya@tectika.com");
        Assert.IsType<ForbidResult>(await other.Update("b1", "t1", created.Id, new UpdateCommentRequest("hack", null), default));
    }

    [Fact]
    public async Task Delete_by_author_soft_deletes()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var created = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "bye", null), default)).Value!;

        Assert.IsType<OkObjectResult>(await ctrl.Delete("b1", "t1", created.Id, default));
        var reloaded = await cosmos.GetCommentAsync("t1", created.Id);
        Assert.NotNull(reloaded!.DeletedAt);   // tombstone, not removed
    }
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: FAIL — no `Update`/`Delete` methods.

- [ ] **Step 3: Implement edit + delete**

Add to `CommentsController`:

```csharp
    [HttpPut("{commentId}")]
    public async Task<IActionResult> Update(string boardId, string taskId, string commentId, [FromBody] UpdateCommentRequest req, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");
        if (comment.AuthorId != UserId) return Forbid();

        var body = (req.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest("Comment body is required.");
        if (comment.Kind == CommentKinds.Note && req.NoteType is not null && !NoteTypes.All.Contains(req.NoteType))
            return BadRequest("Invalid noteType.");

        comment.Body = body;
        if (comment.Kind == CommentKinds.Note && req.NoteType is not null) comment.NoteType = req.NoteType;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        comment.EditedBy = UserId;
        comment.Mentions = comment.Mentions; // mentions are re-sent client-side on edit via create-style flow if needed

        var saved = await _cosmos.UpsertCommentAsync(comment, ct);
        await NotifyMentionsAsync(saved, ct);
        return Ok(saved);
    }

    [HttpDelete("{commentId}")]
    public async Task<IActionResult> Delete(string boardId, string taskId, string commentId, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");
        if (comment.AuthorId != UserId) return Forbid();

        comment.DeletedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpsertCommentAsync(comment, ct);
        return Ok(new { deleted = true });
    }
```

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/CommentsController.cs tests/TectikaAgents.Tests/CommentsControllerTests.cs
git commit -m "feat(api): edit + soft-delete comments (author-only)"
```

---

### Task 7: Reactions toggle + share toggle

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/CommentsController.cs`
- Modify: `tests/TectikaAgents.Tests/CommentsControllerTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `CommentsControllerTests`:

```csharp
    [Fact]
    public async Task React_toggles_user_under_emoji()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var c = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "x", null), default)).Value!;

        var on = (TaskComment)((OkObjectResult)await ctrl.React("b1", "t1", c.Id, new ReactionRequest("👍"), default)).Value!;
        Assert.Contains("eli@tectika.com", on.Reactions["👍"]);

        var off = (TaskComment)((OkObjectResult)await ctrl.React("b1", "t1", c.Id, new ReactionRequest("👍"), default)).Value!;
        Assert.False(off.Reactions.ContainsKey("👍") && off.Reactions["👍"].Contains("eli@tectika.com"));
    }

    [Fact]
    public async Task Share_sets_flag_and_stamps_on_notes_only()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var note = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("note", "decision", "ship it", null), default)).Value!;

        var shared = (TaskComment)((OkObjectResult)await ctrl.Share("b1", "t1", note.Id, new ShareRequest(true), default)).Value!;
        Assert.True(shared.SharedWithAgent);
        Assert.Equal("eli@tectika.com", shared.SharedBy);
        Assert.NotNull(shared.SharedAt);
    }

    [Fact]
    public async Task Share_rejected_for_message_kind()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var msg = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "not a note", null), default)).Value!;

        Assert.IsType<BadRequestObjectResult>(await ctrl.Share("b1", "t1", msg.Id, new ShareRequest(true), default));
    }
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: FAIL — no `React`/`Share`.

- [ ] **Step 3: Implement reactions + share**

Add to `CommentsController`:

```csharp
    [HttpPost("{commentId}/reactions")]
    public async Task<IActionResult> React(string boardId, string taskId, string commentId, [FromBody] ReactionRequest req, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        if (string.IsNullOrWhiteSpace(req.Emoji)) return BadRequest("Emoji is required.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");

        var users = comment.Reactions.TryGetValue(req.Emoji, out var list) ? list : new List<string>();
        if (users.Contains(UserId)) users.Remove(UserId);
        else users.Add(UserId);

        if (users.Count == 0) comment.Reactions.Remove(req.Emoji);
        else comment.Reactions[req.Emoji] = users;

        return Ok(await _cosmos.UpsertCommentAsync(comment, ct));
    }

    [HttpPost("{commentId}/share")]
    public async Task<IActionResult> Share(string boardId, string taskId, string commentId, [FromBody] ShareRequest req, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var comment = await _cosmos.GetCommentAsync(taskId, commentId, ct);
        if (comment is null || comment.DeletedAt is not null) return NotFound("Comment not found.");
        if (comment.Kind != CommentKinds.Note) return BadRequest("Only notes can be shared with the agent.");

        comment.SharedWithAgent = req.Shared;
        if (req.Shared)
        {
            comment.SharedAt = DateTimeOffset.UtcNow;
            comment.SharedBy = UserId;
        }
        return Ok(await _cosmos.UpsertCommentAsync(comment, ct));
    }
```

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/CommentsController.cs tests/TectikaAgents.Tests/CommentsControllerTests.cs
git commit -m "feat(api): reaction toggle + share-with-agent toggle (notes only)"
```

---

### Task 8: Mark-read endpoint

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/CommentsController.cs`
- Modify: `tests/TectikaAgents.Tests/CommentsControllerTests.cs`

- [ ] **Step 1: Add failing test**

Append:

```csharp
    [Fact]
    public async Task MarkRead_records_marker_for_task()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");

        var res = Assert.IsType<OkObjectResult>(await ctrl.MarkRead("b1", "t1", default));
        Assert.NotNull(res.Value);
    }
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: FAIL — no `MarkRead`.

- [ ] **Step 3: Implement**

Add to `CommentsController`:

```csharp
    [HttpPost("read")]
    public async Task<IActionResult> MarkRead(string boardId, string taskId, CancellationToken ct)
    {
        if (await AuthorizedTaskAsync(boardId, taskId, ct) is null) return NotFound("Task not found.");
        var settings = await _userSettings.GetOrCreateAsync(UserId, ct);
        var now = DateTimeOffset.UtcNow;
        settings.TaskReadMarkers[taskId] = now;
        await _userSettings.UpsertAsync(settings, ct);
        return Ok(new { lastReadAt = now });
    }
```

> The frontend computes the unread badge from `lastReadAt` + the comment list. `lastReadAt` is returned by `MarkRead`; the client also reads its current marker via the existing user-settings fetch (the `GET /api/notifications` flow already surfaces `UserSettingsDocument`; if a dedicated read is needed, the list response can be extended — but v1 computes unread purely client-side from the moment the tab opens, then calls MarkRead, see Task 16).

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/CommentsController.cs tests/TectikaAgents.Tests/CommentsControllerTests.cs
git commit -m "feat(api): mark Team tab read (per-task read marker)"
```

---

## Phase 3 — Backend mentions

### Task 9: `RecipientUserId` + mention notifications

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/NotificationDocument.cs`
- Modify: `src/api/TectikaAgents.Api/Services/NotificationRepository.cs` (filter recipient)
- Modify: `src/api/TectikaAgents.Api/Controllers/NotificationsController.cs` (pass UserId)
- Modify: `src/api/TectikaAgents.Api/Controllers/CommentsController.cs` (real `NotifyMentionsAsync`)
- Modify: `tests/TectikaAgents.Tests/CommentsControllerTests.cs`

- [ ] **Step 1: Add `RecipientUserId` to the model**

In `NotificationDocument.cs` add:

```csharp
    /// <summary>If set, this notification targets a single user (e.g. an @-mention).
    /// Null = tenant-wide (existing behavior, visible to all).</summary>
    [JsonPropertyName("recipientUserId")]
    public string? RecipientUserId { get; set; }
```

- [ ] **Step 2: Write failing test (mention creates a targeted notification)**

Append to `CommentsControllerTests`. First expose the saved notifications from the test double by capturing the repo:

```csharp
    [Fact]
    public async Task Create_with_mentions_saves_targeted_notifications()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var notifs = new TestNotificationRepo();
        var ctrl = new CommentsController(cosmos, new TestUserSettingsRepo(), notifs, NullLogger<CommentsController>.Instance);
        ctrl.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tid", "default"),
                    new Claim("preferred_username", "eli@tectika.com"),
                }, "Test"))
            }
        };

        await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "ping @maya", new List<string> { "maya@tectika.com" }), default);

        var n = Assert.Single(notifs.Saved);
        Assert.Equal("maya@tectika.com", n.RecipientUserId);
        Assert.Equal("default", n.TenantId);
        Assert.Equal("t1", n.TaskId);
        Assert.Equal("mention", n.Type);
    }

    [Fact]
    public async Task Create_does_not_notify_self_mention()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var notifs = new TestNotificationRepo();
        var ctrl = new CommentsController(cosmos, new TestUserSettingsRepo(), notifs, NullLogger<CommentsController>.Instance);
        ctrl.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tid", "default"),
                    new Claim("preferred_username", "eli@tectika.com"),
                }, "Test"))
            }
        };

        await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "note to self @eli", new List<string> { "eli@tectika.com" }), default);

        Assert.Empty(notifs.Saved);
    }
```

- [ ] **Step 3: Run to verify fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CommentsControllerTests`
Expected: FAIL — `NotifyMentionsAsync` is a no-op; `Saved` empty.

- [ ] **Step 4: Implement real `NotifyMentionsAsync`**

Replace the stub in `CommentsController`:

```csharp
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private async Task NotifyMentionsAsync(TaskComment comment, CancellationToken ct)
    {
        foreach (var recipient in comment.Mentions.Distinct().Where(m => m != comment.AuthorId))
        {
            await _notifications.SaveAsync(new NotificationDocument
            {
                TenantId = comment.TenantId,
                RecipientUserId = recipient,
                Type = "mention",
                Title = $"{comment.AuthorId} mentioned you",
                Subtitle = Truncate(comment.Body, 120),
                BoardId = comment.BoardId,
                TaskId = comment.TaskId,
                SourceEventType = "team_comment_mention",
            }, ct);
        }
    }
```

- [ ] **Step 5: Filter recipient in `NotificationRepository.GetRecentAsync`**

Update the query in `NotificationRepository.cs` to accept a `userId` and filter (null recipient = visible to all):

```csharp
    public virtual async Task<IReadOnlyList<NotificationDocument>> GetRecentAsync(string tenantId, string userId, int limit = 50, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP @limit * FROM c WHERE c.tenantId = @tenantId " +
            "AND (NOT IS_DEFINED(c.recipientUserId) OR c.recipientUserId = null OR c.recipientUserId = @userId) " +
            "ORDER BY c.timestamp DESC")
            .WithParameter("@limit", limit)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@userId", userId);

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) };
        var iterator = Container.GetItemQueryIterator<NotificationDocument>(query, requestOptions: options);
        var results = new List<NotificationDocument>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }
```

- [ ] **Step 6: Update the caller in `NotificationsController.GetNotifications`**

Change the call to pass `UserId`:

```csharp
        var notifications = await _notificationRepo.GetRecentAsync(TenantId, UserId, limit, ct);
```

- [ ] **Step 7: Run full backend test suite**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: PASS (all, including the 2 new mention tests). If any existing test called `GetRecentAsync(tenantId, limit)`, update it to the new signature.

- [ ] **Step 8: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/NotificationDocument.cs src/api/TectikaAgents.Api/Services/NotificationRepository.cs src/api/TectikaAgents.Api/Controllers/NotificationsController.cs src/api/TectikaAgents.Api/Controllers/CommentsController.cs tests/TectikaAgents.Tests/CommentsControllerTests.cs
git commit -m "feat(api): per-recipient @-mention notifications for team comments"
```

---

## Phase 4 — Frontend types + API client

### Task 10: Extend the `Comment` type

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts` (Comment, ~lines 517-525)

- [ ] **Step 1: Replace the `Comment` interface**

```typescript
export type CommentKind = 'note' | 'message';
export type NoteType = 'decision' | 'open_question' | 'note';

export interface Comment {
  id: string;
  taskId: string;
  boardId: string;
  kind: CommentKind;
  noteType?: NoteType;          // notes only
  authorId: string;
  body: string;
  mentions: string[];
  reactions?: Record<string, string[]>;   // emoji -> userIds
  createdAt: string;
  updatedAt?: string;
  editedBy?: string;
  deletedAt?: string;
  sharedWithAgent?: boolean;
  sharedAt?: string;
  sharedBy?: string;
}
```

- [ ] **Step 2: Stop seeding demo comments (keep signature stable)**

In `src/web/tectika-board/src/lib/collaboration.ts`, in `seedCollaboration`, remove the two `comments.push({...})` blocks so it returns `comments: []` (leave `activity` seeding intact). This removes now-type-incompatible literals without changing the function's return shape.

- [ ] **Step 3: Typecheck**

Run: `npm run build --prefix src/web/tectika-board` (or `npx tsc --noEmit` in that dir)
Expected: No type errors. If any other file constructs a `Comment` literal, update it to include `boardId` + `kind`.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/collaboration.ts
git commit -m "feat(web): extend Comment type for notes+discussion; drop demo comment seed"
```

---

### Task 11: `api.comments` client + test

**Files:**
- Modify: `src/web/tectika-board/src/lib/api.ts`
- Test: `src/web/tectika-board/src/lib/__tests__/comments-api.test.ts` (create)

- [ ] **Step 1: Write the failing test (follow the existing api-client test pattern)**

Create `src/web/tectika-board/src/lib/__tests__/comments-api.test.ts`. Model it on the existing `__tests__/preview-api.test.ts` / `board-settings-api.test.ts` (which stub `global.fetch`). Example:

```typescript
import { test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { api } from '../api.ts';

let calls: { url: string; init?: RequestInit }[] = [];
const realFetch = global.fetch;

beforeEach(() => {
  calls = [];
  global.fetch = (async (url: string, init?: RequestInit) => {
    calls.push({ url: String(url), init });
    return new Response(JSON.stringify([]), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;
});
afterEach(() => { global.fetch = realFetch; });

test('list hits the comments collection endpoint', async () => {
  await api.comments.list('b1', 't1');
  assert.match(calls[0].url, /\/api\/boards\/b1\/tasks\/t1\/comments$/);
  assert.equal(calls[0].init?.method ?? 'GET', 'GET');
});

test('create posts kind/body/mentions', async () => {
  await api.comments.create('b1', 't1', { kind: 'note', noteType: 'decision', body: 'x', mentions: ['maya@tectika.com'] });
  const { url, init } = calls[0];
  assert.match(url, /\/comments$/);
  assert.equal(init?.method, 'POST');
  assert.deepEqual(JSON.parse(String(init?.body)), { kind: 'note', noteType: 'decision', body: 'x', mentions: ['maya@tectika.com'] });
});

test('share posts the shared flag', async () => {
  await api.comments.share('b1', 't1', 'c1', true);
  assert.match(calls[0].url, /\/comments\/c1\/share$/);
  assert.deepEqual(JSON.parse(String(calls[0].init?.body)), { shared: true });
});
```

> Confirm the exact stub style against `preview-api.test.ts`; match whatever it does (it may mock a module-level `fetchApi` rather than `global.fetch`). Use the existing test's approach verbatim.

- [ ] **Step 2: Run to verify fail**

Run: `npm test --prefix src/web/tectika-board`
Expected: FAIL — `api.comments` is undefined.

- [ ] **Step 3: Add the `comments` group to `api.ts`**

Add inside the `api` object (next to `tasks`):

```typescript
  comments: {
    list: (boardId: string, taskId: string) =>
      fetchApi<Comment[]>(`/api/boards/${boardId}/tasks/${taskId}/comments`),
    create: (boardId: string, taskId: string, input: { kind: CommentKind; noteType?: NoteType; body: string; mentions: string[] }) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments`, { method: 'POST', body: JSON.stringify(input) }),
    update: (boardId: string, taskId: string, commentId: string, input: { body: string; noteType?: NoteType }) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}`, { method: 'PUT', body: JSON.stringify(input) }),
    remove: (boardId: string, taskId: string, commentId: string) =>
      fetchApi<{ deleted: boolean }>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}`, { method: 'DELETE' }),
    react: (boardId: string, taskId: string, commentId: string, emoji: string) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}/reactions`, { method: 'POST', body: JSON.stringify({ emoji }) }),
    share: (boardId: string, taskId: string, commentId: string, shared: boolean) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}/share`, { method: 'POST', body: JSON.stringify({ shared }) }),
    markRead: (boardId: string, taskId: string) =>
      fetchApi<{ lastReadAt: string }>(`/api/boards/${boardId}/tasks/${taskId}/comments/read`, { method: 'POST' }),
  },
```

Add `CommentKind, NoteType` to the existing type import from `./types`.

- [ ] **Step 4: Run — verify pass**

Run: `npm test --prefix src/web/tectika-board`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/api.ts src/web/tectika-board/src/lib/__tests__/comments-api.test.ts
git commit -m "feat(web): api.comments client + tests"
```

---

## Phase 5 — Frontend shared markdown + mention rendering

### Task 12: Extract `Markdown` to a shared module

**Files:**
- Create: `src/web/tectika-board/src/lib/markdown.tsx`
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` (remove local `Markdown`/`inlineBold`, import from new module)

- [ ] **Step 1: Create the shared module**

Move the `Markdown` and `inlineBold` functions verbatim from `ItemPanel.tsx` (~lines 1030-1049) into `src/web/tectika-board/src/lib/markdown.tsx`, exporting `Markdown`:

```tsx
import React from 'react';

// minimal markdown: headings, lists, code fences, bold/inline-code. XSS-safe:
// all content routes through React text nodes (no dangerouslySetInnerHTML).
export function Markdown({ text }: { text: string }) {
  // ... exact body moved from ItemPanel.tsx ...
}

function inlineBold(s: string) {
  // ... exact body moved from ItemPanel.tsx ...
}
```

- [ ] **Step 2: Update `ItemPanel.tsx`**

Delete the local `Markdown`/`inlineBold` definitions and add at the top: `import { Markdown } from '../../lib/markdown';` (adjust relative path). Leave all existing `<Markdown .../>` usages unchanged.

- [ ] **Step 3: Typecheck + run existing tests**

Run: `npm run build --prefix src/web/tectika-board && npm test --prefix src/web/tectika-board`
Expected: Build clean, tests pass (no behavior change).

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/markdown.tsx src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "refactor(web): extract Markdown renderer to lib/markdown for reuse"
```

---

### Task 13: Mention parse + highlight helpers

**Files:**
- Create: `src/web/tectika-board/src/lib/team-notes.ts`
- Test: `src/web/tectika-board/src/lib/team-notes.test.ts` (create)

- [ ] **Step 1: Write failing tests**

Create `src/web/tectika-board/src/lib/team-notes.test.ts`:

```typescript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseMentions, countUnread, splitComments } from './team-notes.ts';
import type { Comment, Person } from './types.ts';

const roster: Person[] = [
  { id: 'eli@tectika.com', name: 'Eli Weber', kind: 'Human', hex: '#0073ea' },
  { id: 'maya@tectika.com', name: 'Maya Cohen', kind: 'Human', hex: '#e2445c' },
];

test('parseMentions resolves @first-name and @email to ids', () => {
  assert.deepEqual(parseMentions('hey @maya and @eli@tectika.com', roster).sort(),
    ['eli@tectika.com', 'maya@tectika.com']);
});

test('parseMentions ignores unknown handles', () => {
  assert.deepEqual(parseMentions('hi @nobody', roster), []);
});

const mk = (over: Partial<Comment>): Comment => ({
  id: Math.random().toString(), taskId: 't1', boardId: 'b1', kind: 'message',
  authorId: 'maya@tectika.com', body: 'x', mentions: [], createdAt: '2026-06-29T10:00:00Z', ...over,
});

test('countUnread counts others\' comments after lastReadAt', () => {
  const comments = [
    mk({ createdAt: '2026-06-29T09:00:00Z' }),                                  // before read
    mk({ createdAt: '2026-06-29T11:00:00Z' }),                                  // after, by other
    mk({ createdAt: '2026-06-29T12:00:00Z', authorId: 'eli@tectika.com' }),     // after, by me (excluded)
  ];
  assert.equal(countUnread(comments, '2026-06-29T10:30:00Z', 'eli@tectika.com'), 1);
});

test('countUnread excludes deleted and counts all when never read', () => {
  const comments = [mk({}), mk({ deletedAt: '2026-06-29T10:05:00Z' })];
  assert.equal(countUnread(comments, undefined, 'eli@tectika.com'), 1);
});

test('splitComments separates notes (non-deleted) from messages', () => {
  const comments = [
    mk({ kind: 'note', noteType: 'decision' }),
    mk({ kind: 'note', deletedAt: '2026-06-29T10:05:00Z' }),
    mk({ kind: 'message' }),
  ];
  const { notes, messages } = splitComments(comments);
  assert.equal(notes.length, 1);
  assert.equal(messages.length, 1);
});
```

- [ ] **Step 2: Run to verify fail**

Run: `npm test --prefix src/web/tectika-board`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the helpers**

Create `src/web/tectika-board/src/lib/team-notes.ts`:

```typescript
import type { Comment, Person } from './types.ts';

/** Resolve @handles in a body to known person ids. Matches @email or @first-name (case-insensitive). */
export function parseMentions(body: string, roster: Person[]): string[] {
  const handles = body.match(/@[\w.+-]+(@[\w.-]+)?/g) ?? [];
  const ids = new Set<string>();
  for (const raw of handles) {
    const h = raw.slice(1).toLowerCase();
    const match = roster.find(p =>
      p.id.toLowerCase() === h ||
      p.name.split(' ')[0].toLowerCase() === h);
    if (match) ids.add(match.id);
  }
  return [...ids];
}

/** Count non-deleted comments authored by others, created after lastReadAt (all if never read). */
export function countUnread(comments: Comment[], lastReadAt: string | undefined, currentUserId: string): number {
  const since = lastReadAt ? Date.parse(lastReadAt) : 0;
  return comments.filter(c =>
    !c.deletedAt &&
    c.authorId !== currentUserId &&
    Date.parse(c.createdAt) > since).length;
}

/** Split into the Notes zone (non-deleted notes) and the Discussion feed (messages, incl. tombstones). */
export function splitComments(comments: Comment[]): { notes: Comment[]; messages: Comment[] } {
  const notes = comments.filter(c => c.kind === 'note' && !c.deletedAt);
  const messages = comments.filter(c => c.kind === 'message');
  return { notes, messages };
}
```

- [ ] **Step 4: Run — verify pass**

Run: `npm test --prefix src/web/tectika-board`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/team-notes.ts src/web/tectika-board/src/lib/team-notes.test.ts
git commit -m "feat(web): team-notes pure helpers (mentions, unread, split) + tests"
```

---

## Phase 6 — Frontend TeamTab

### Task 14: `TeamTab` component — Notes + Discussion + composer

**Files:**
- Create: `src/web/tectika-board/src/components/workspace/TeamTab.tsx`

This is the largest task. It renders both zones, polls every 4s while visible, posts optimistically, and exposes reactions / edit / delete / share. It uses `useBoard()` for `peopleById`, `CURRENT_USER` for identity, `Avatar`/`Button` primitives, `Markdown`, `relativeTime`, and the `team-notes` helpers.

- [ ] **Step 1: Create the component**

```tsx
'use client';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AgentTask, Comment, CommentKind, NoteType } from '../../lib/types';
import { api } from '../../lib/api';
import { useBoard } from '../../lib/board-context';
import { CURRENT_USER } from '../../lib/collaboration';
import { Avatar, Button } from '../ui/primitives';
import { Markdown } from '../../lib/markdown';
import { relativeTime } from '../../lib/format';
import { parseMentions, splitComments } from '../../lib/team-notes';

const NOTE_TYPE_META: Record<NoteType, { label: string; hex: string }> = {
  decision: { label: 'Decision', hex: '#f0a020' },
  open_question: { label: 'Open Q', hex: '#e2445c' },
  note: { label: 'Note', hex: '#676879' },
};
const REACTIONS = ['👍', '🎉', '👀', '🙏'];

export function TeamTab({ task }: { task: AgentTask }) {
  const { peopleById, people } = useBoard();
  const me = CURRENT_USER.id;
  const [comments, setComments] = useState<Comment[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const seq = useRef(0);

  const load = useCallback(async () => {
    const id = ++seq.current;
    try {
      const list = await api.comments.list(task.boardId, task.id);
      if (id === seq.current) { setComments(list); setLoaded(true); }
    } catch { /* keep last good state; polling retries */ }
  }, [task.boardId, task.id]);

  // initial load + mark read; 4s poll while visible (Chat/CLI-bridge pattern).
  useEffect(() => {
    load();
    api.comments.markRead(task.boardId, task.id).catch(() => {});
    const poll = setInterval(() => {
      if (document.visibilityState === 'hidden') return;
      load();
    }, 4000);
    return () => clearInterval(poll);
  }, [load, task.boardId, task.id]);

  const { notes, messages } = useMemo(() => splitComments(comments), [comments]);

  const post = async (kind: CommentKind, body: string, noteType?: NoteType) => {
    const text = body.trim();
    if (!text || sending) return;
    setSending(true);
    const mentions = parseMentions(text, people);
    // optimistic
    const optimistic: Comment = {
      id: `tmp-${Date.now()}`, taskId: task.id, boardId: task.boardId, kind,
      noteType, authorId: me, body: text, mentions, reactions: {}, createdAt: new Date().toISOString(),
    };
    setComments(prev => [...prev, optimistic]);
    if (kind === 'message') setDraft('');
    try {
      const saved = await api.comments.create(task.boardId, task.id, { kind, noteType, body: text, mentions });
      setComments(prev => prev.map(c => (c.id === optimistic.id ? saved : c)));
    } catch {
      setComments(prev => prev.filter(c => c.id !== optimistic.id));   // rollback
      if (kind === 'message') setDraft(text);
    } finally {
      setSending(false);
    }
  };

  const toggleReaction = async (c: Comment, emoji: string) => {
    try { const updated = await api.comments.react(task.boardId, task.id, c.id, emoji); setComments(prev => prev.map(x => x.id === c.id ? updated : x)); }
    catch { /* polling reconciles */ }
  };
  const toggleShare = async (c: Comment) => {
    try { const updated = await api.comments.share(task.boardId, task.id, c.id, !c.sharedWithAgent); setComments(prev => prev.map(x => x.id === c.id ? updated : x)); }
    catch { /* ignore */ }
  };
  const remove = async (c: Comment) => {
    if (!confirm('Delete this?')) return;
    setComments(prev => prev.map(x => x.id === c.id ? { ...x, deletedAt: new Date().toISOString() } : x));
    try { await api.comments.remove(task.boardId, task.id, c.id); } catch { load(); }
  };

  if (!loaded) return <div className="p-4 text-[13px] text-[var(--muted)]">Loading…</div>;

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 overflow-auto">
        {/* NOTES */}
        <section className="p-3.5 border-b border-[var(--border)]">
          <div className="flex items-center justify-between mb-2">
            <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">📌 Notes</span>
            <AddNote onAdd={(body, noteType) => post('note', body, noteType)} />
          </div>
          {notes.length === 0
            ? <p className="text-[12px] text-[var(--muted)]">No notes yet — capture a decision or open question.</p>
            : <div className="flex flex-col gap-2">{notes.map(n =>
                <NoteCard key={n.id} note={n} me={me} authorName={peopleById[n.authorId]?.name ?? n.authorId}
                  onShare={() => toggleShare(n)} onDelete={() => remove(n)} />)}</div>}
        </section>

        {/* DISCUSSION */}
        <section className="p-3.5 flex flex-col gap-3.5">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">💬 Discussion</span>
          {messages.length === 0
            ? <p className="text-[12px] text-[var(--muted)]">No messages yet.</p>
            : messages.map(m =>
                <MessageRow key={m.id} comment={m} me={me} person={peopleById[m.authorId]}
                  reactions={REACTIONS} onReact={(e) => toggleReaction(m, e)} onDelete={() => remove(m)} />)}
        </section>
      </div>

      {/* COMPOSER */}
      <div className="border-t border-[var(--border)] p-3.5 flex gap-2 items-end">
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) post('message', draft); }}
          placeholder="Message the team…  @ to mention"
          rows={2}
          className="flex-1 bg-[var(--surface)] border border-[var(--border)] rounded-lg p-2 text-[13px] outline-none resize-none focus:border-[var(--primary)]"
        />
        <Button variant="primary" size="sm" disabled={sending || !draft.trim()} onClick={() => post('message', draft)}>Send</Button>
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Add the subcomponents (same file)**

```tsx
function AddNote({ onAdd }: { onAdd: (body: string, noteType: NoteType) => void }) {
  const [open, setOpen] = useState(false);
  const [body, setBody] = useState('');
  const [noteType, setNoteType] = useState<NoteType>('note');
  if (!open) return <button className="text-[11px] text-[var(--primary)]" onClick={() => setOpen(true)}>+ Add note</button>;
  return (
    <div className="w-full mt-1">
      <div className="flex gap-1.5 mb-1.5">
        {(['decision', 'open_question', 'note'] as NoteType[]).map(t => (
          <button key={t} onClick={() => setNoteType(t)}
            className={`text-[10px] px-2 py-0.5 rounded ${noteType === t ? 'text-white' : 'text-[var(--muted)] border border-[var(--border)]'}`}
            style={noteType === t ? { background: NOTE_TYPE_META[t].hex } : undefined}>{NOTE_TYPE_META[t].label}</button>
        ))}
      </div>
      <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={2} autoFocus
        className="w-full bg-[var(--surface)] border border-[var(--border)] rounded-lg p-2 text-[13px] outline-none resize-none focus:border-[var(--primary)]" />
      <div className="flex gap-2 mt-1.5">
        <Button variant="primary" size="sm" disabled={!body.trim()} onClick={() => { onAdd(body, noteType); setBody(''); setOpen(false); }}>Save note</Button>
        <Button variant="ghost" size="sm" onClick={() => { setBody(''); setOpen(false); }}>Cancel</Button>
      </div>
    </div>
  );
}

function NoteCard({ note, me, authorName, onShare, onDelete }: {
  note: Comment; me: string; authorName: string; onShare: () => void; onDelete: () => void;
}) {
  const meta = NOTE_TYPE_META[note.noteType ?? 'note'];
  return (
    <div className="rounded-lg p-2.5" style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
      <div className="flex items-center justify-between mb-1">
        <span className="text-[9px] uppercase tracking-wide px-1.5 py-0.5 rounded text-white" style={{ background: meta.hex }}>{meta.label}</span>
        <button onClick={onShare} className="text-[11px] font-semibold" style={{ color: note.sharedWithAgent ? '#6b4ce6' : 'var(--muted)' }}>
          {note.sharedWithAgent ? '✓ Shared with agent' : '⤳ Share with agent'}
        </button>
      </div>
      <div className="text-[12px]"><Markdown text={note.body} /></div>
      <div className="text-[10px] text-[var(--muted)] mt-1.5 flex gap-2">
        <span>{note.editedBy ? `edited by ${note.editedBy}` : authorName} · {relativeTime(note.updatedAt ?? note.createdAt)}</span>
        {note.authorId === me && <button onClick={onDelete} className="hover:text-[var(--foreground)]">delete</button>}
      </div>
    </div>
  );
}

function MessageRow({ comment, me, person, reactions, onReact, onDelete }: {
  comment: Comment; me: string; person?: { name: string; hex: string; kind: string }; reactions: string[];
  onReact: (emoji: string) => void; onDelete: () => void;
}) {
  const [hover, setHover] = useState(false);
  if (comment.deletedAt) return <div className="text-[12px] text-[var(--muted)] italic pl-9">message deleted</div>;
  return (
    <div className="flex gap-2.5" onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}>
      <Avatar person={person as never} name={person?.name ?? comment.authorId} hex={person?.hex} size={26} />
      <div className="flex-1 min-w-0">
        <div className="text-[12px]">
          <strong>{person?.name ?? comment.authorId}</strong>{' '}
          <span className="text-[var(--muted)]">{relativeTime(comment.createdAt)}</span>
          {hover && (
            <span className="ml-2">
              {reactions.map(e => <button key={e} className="opacity-60 hover:opacity-100" onClick={() => onReact(e)}>{e}</button>)}
              {comment.authorId === me && <button className="ml-1 text-[var(--muted)] hover:text-[var(--foreground)]" onClick={onDelete}>✕</button>}
            </span>
          )}
        </div>
        <div className="text-[12px]"><Markdown text={comment.body} /></div>
        {comment.reactions && Object.keys(comment.reactions).length > 0 && (
          <div className="flex gap-1.5 mt-1">
            {Object.entries(comment.reactions).map(([emoji, users]) => (
              <button key={emoji} onClick={() => onReact(emoji)}
                className="text-[11px] px-1.5 rounded-full border border-[var(--border)] bg-[var(--background)]">{emoji} {users.length}</button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
```

> **Mention highlighting note:** the `Markdown` renderer does not linkify `@mentions`. v1 renders the raw `@handle` text (still readable). If inline highlighting is wanted, add a small pass that wraps known handles in a styled `<span>` — defer unless requested; it's pure polish.

- [ ] **Step 3: Typecheck**

Run: `npm run build --prefix src/web/tectika-board`
Expected: clean (TeamTab not yet imported — that's Task 15).

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/TeamTab.tsx
git commit -m "feat(web): TeamTab component (notes zone, discussion feed, composer, polling)"
```

---

### Task 15: Wire `TeamTab` into `ItemPanel`

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` (Tab type ~line 24, tab array ~line 84, render block ~line 88-93)

- [ ] **Step 1: Add `'team'` to the `Tab` union (line 24)**

```typescript
type Tab = 'chat' | 'activity' | 'details' | 'bridge' | 'team';
```

- [ ] **Step 2: Add to the tab array + label (line 84-86)**

Change the array to include `'team'` and the label ternary:

```typescript
{(['chat', 'activity', 'details', 'bridge', 'team'] as Tab[]).map(t => (
  <button key={t} onClick={() => setTab(t)} className={`px-3 py-2.5 text-[13px] font-medium capitalize border-b-2 -mb-px transition-colors shrink-0 ${tab === t ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>{t === 'chat' ? 'Chat' : t === 'bridge' ? 'CLI Bridge' : t === 'team' ? 'Team' : t}</button>
))}
```

- [ ] **Step 3: Add the render line (after the bridge line, ~line 92)**

```tsx
  {tab === 'team' && <TeamTab task={task} />}
```

- [ ] **Step 4: Import TeamTab at the top of `ItemPanel.tsx`**

```tsx
import { TeamTab } from './TeamTab';
```

- [ ] **Step 5: Build + run all frontend tests**

Run: `npm run build --prefix src/web/tectika-board && npm test --prefix src/web/tectika-board`
Expected: Build clean; tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): add Team tab to the task panel"
```

---

### Task 16: Unread badge on the Team tab button

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`

The badge shows the count of unread comments on the Team tab button while another tab is active. Compute it by fetching the comment list + the user's read marker. To keep `ItemPanel` simple and avoid a second source of truth, fetch the count alongside the panel's existing data.

- [ ] **Step 1: Add a lightweight unread fetch in ItemPanel**

Near the panel's other state, add:

```tsx
const [teamUnread, setTeamUnread] = useState(0);
useEffect(() => {
  let alive = true;
  const tick = async () => {
    try {
      const list = await api.comments.list(task.boardId, task.id);
      // lastReadAt: persisted server-side; for the badge we treat "since panel mount" as a
      // pragmatic baseline and rely on TeamTab's markRead to reset. Count others' undeleted comments.
      if (alive) setTeamUnread(countUnread(list, mountedAtRef.current, CURRENT_USER.id));
    } catch { /* ignore */ }
  };
  tick();
  const iv = setInterval(() => { if (document.visibilityState !== 'hidden') tick(); }, 4000);
  return () => { alive = false; clearInterval(iv); };
}, [task.boardId, task.id]);
```

with `const mountedAtRef = useRef(new Date().toISOString());` and imports `countUnread` from `../../lib/team-notes` and `CURRENT_USER` from `../../lib/collaboration`.

> **Refinement option (recommended for accuracy):** instead of "since mount", return the caller's `taskReadMarkers[taskId]` from `GET /comments` (add it to the response payload as `{ comments, lastReadAt }`). If you do, update `api.comments.list` return type and `TeamTab.load` accordingly, and compute `countUnread(list, lastReadAt, me)` everywhere. The "since mount" baseline ships without backend changes; the marker-based version is exact. Pick one and use it consistently. Decide at implementation; this plan's Task 8 already persists the marker, so the marker-based version is a small add.

- [ ] **Step 2: Render the badge on the Team button**

In the tab `.map`, when `t === 'team' && teamUnread > 0`, render a small count pill after the label:

```tsx
{t === 'team' && teamUnread > 0 && (
  <span className="ml-1.5 bg-[#e2445c] text-white text-[10px] rounded-full px-1.5">{teamUnread}</span>
)}
```

Clear it when the Team tab opens: in the `onClick` for `setTab(t)`, when `t === 'team'` set `setTeamUnread(0)`.

- [ ] **Step 3: Build + run**

Run: `npm run build --prefix src/web/tectika-board && npm test --prefix src/web/tectika-board`
Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): unread badge on the Team tab"
```

---

### Task 17: Manual visual QA (no component-test infra)

Because the web app has no React Testing Library, verify the component interactively.

- [ ] **Step 1: Run the app per the project's visual-QA setup**

Launch the board (mock DB mode, `eli@tectika.com`) and the API locally, open a task panel, and check:
- Team tab appears 5th, labeled "Team".
- Add a note (each type), edit/delete your own, toggle "Share with agent" (notes only).
- Post discussion messages (Cmd/Ctrl+Enter), react, delete your own (shows tombstone).
- @-mention a teammate; confirm a targeted notification appears in the notifications surface.
- Open another tab, have a second identity post, confirm the unread badge increments and clears on open.

- [ ] **Step 2: Note results in the PR description.** No commit unless fixes are needed.

---

## Phase 7 — Agent `read_team_notes` tool (independently shippable)

> Verify item #5 resolved: the tool-loop is live and stable (`tools-v10`). This phase touches the agent runtime + workflows Cosmos path and requires a `TectikaToolSchema.Version` bump to republish agents. It can ship after the human-facing tab.

### Task 18: Workflows-side read of shared notes

**Files:**
- Modify: `src/workflows/Services/WorkflowCosmosService.cs` (add a read method)
- Modify: `src/core/TectikaAgents.Core/Interfaces/IProjectExplorer.cs`
- Modify: `src/workflows/Services/BoardProjectExplorer.cs`

- [ ] **Step 1: Add a shared-notes query to `WorkflowCosmosService`**

Following that service's existing Cosmos query pattern (mirror `GetBoardTasksAsync`), add:

```csharp
public async Task<IReadOnlyList<TaskComment>> GetSharedTaskNotesAsync(string taskId, CancellationToken ct = default)
{
    var query = new QueryDefinition(
        "SELECT * FROM c WHERE c.taskId = @taskId AND c.kind = 'note' " +
        "AND c.sharedWithAgent = true AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null) " +
        "ORDER BY c.createdAt ASC")
        .WithParameter("@taskId", taskId);
    // use this service's existing container accessor + iterator pattern for the "taskComments" container
    // ... (mirror the existing GetBoardTasksAsync implementation) ...
}
```

> The container name constant is `"taskComments"` (matches `CosmosDbService.TaskCommentsContainer`). Reuse this service's existing iterator helper; do not introduce a new one.

- [ ] **Step 2: Extend `IProjectExplorer`**

```csharp
/// <summary>Notes the team has explicitly shared with the agent on this task.</summary>
Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default);
```

Add the projection record (in the interface's namespace):

```csharp
public sealed record SharedNote(string NoteType, string Body, string Author, DateTimeOffset UpdatedAt);
```

- [ ] **Step 3: Implement in `BoardProjectExplorer` (with scope guard)**

```csharp
public async Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default)
{
    var task = await _cosmos.GetTaskAsync(_boardId, taskId, ct);   // scope guard: task must be on THIS board
    if (task is null) return [];
    var notes = await _cosmos.GetSharedTaskNotesAsync(taskId, ct);
    return notes.Select(n => new SharedNote(
        n.NoteType ?? "note", n.Body, n.AuthorId, n.UpdatedAt ?? n.CreatedAt)).ToList();
}
```

> Confirm `_cosmos` here is the `WorkflowCosmosService` and that `GetTaskAsync(boardId, taskId)` exists on it (the explorer already uses board-scoped task lookups). If the method name differs, use the existing board-scoped task accessor.

- [ ] **Step 4: Build the workflows + core projects**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/workflows/Services/WorkflowCosmosService.cs src/core/TectikaAgents.Core/Interfaces/IProjectExplorer.cs src/workflows/Services/BoardProjectExplorer.cs
git commit -m "feat(workflows): explorer access to agent-shared task notes"
```

---

### Task 19: Tool definition + dispatch + version bump

**Files:**
- Modify: `src/agentruntime/TectikaToolSchema.cs` (Explore section + `Version`)
- Modify: `src/agentruntime/RoundExecutor.cs` (dispatch case)

- [ ] **Step 1: Add the tool definition**

In `TectikaToolSchema.cs`, in the Explore section (after `get_artifact`), add:

```csharp
new("read_team_notes",
    "Read notes the human team has explicitly shared with you on a task (decisions, open questions, context). Use before major decisions or when you need the team's standing guidance.",
    new Dictionary<string, ToolProp> { ["taskId"] = new("string", "The task id.") },
    ["taskId"]),
```

- [ ] **Step 2: Bump the schema version**

Change `Version`:

```csharp
public const string Version = "tools-v11";  // was tools-v10 — added read_team_notes
```

- [ ] **Step 3: Add the dispatch case in `RoundExecutor`**

After the `get_artifact` case:

```csharp
case "read_team_notes":
    outputs.Add(new(call.CallId, await Serialize(explorer.GetSharedNotesAsync(Str(args, "taskId"), ct))));
    traced.Add(new("read_team_notes", Str(args, "taskId"), Summarize(outputs[^1].Output)));
    break;
```

- [ ] **Step 4: Build the agentruntime**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/TectikaToolSchema.cs src/agentruntime/RoundExecutor.cs
git commit -m "feat(tools): read_team_notes board-scoped tool; bump schema v10->v11"
```

---

### Task 20: Agent prompt nudge

**Files:**
- Modify: the agent system/task prompt builder (locate via `grep -rl "get_board_overview" src` → the prompt that lists explore tools, likely in `src/agentruntime/` or a prompt template).

- [ ] **Step 1: Add one line to the explore-tools guidance**

Where the prompt describes the explore tools, add a sentence:

> "If the team has shared notes on this task (decisions, open questions), call `read_team_notes` to read their current guidance before making significant changes."

- [ ] **Step 2: Build + commit**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj`

```bash
git add -A src/agentruntime
git commit -m "feat(tools): nudge agent to consult shared team notes"
```

---

## Phase 8 — Deploy + verify

### Task 21: Full test sweep + infra consistency

- [ ] **Step 1: Backend tests**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: all pass.

- [ ] **Step 2: Frontend tests + build**

Run: `npm test --prefix src/web/tectika-board && npm run build --prefix src/web/tectika-board`
Expected: all pass, build clean.

- [ ] **Step 3: Verify infra/runtime container parity**

Confirm `taskComments` is present in BOTH `CosmosDbService.ContainerDefinitions` and `infra/modules/data.bicep`. (A quick grep for `taskComments` should hit both.)

- [ ] **Step 4: Prod container creation note**

When deploying: create the container explicitly (since `EnsureInfrastructureAsync` swallows failures):

```bash
az cosmosdb sql container create -a <cosmos-account> -d tectikaagents -g <resource-group> -n taskComments --partition-key-path /taskId
```

Then deploy the API (republish picks up the new endpoints) and, for Phase 7, redeploy workflows/agentruntime so agents republish under `tools-v11`.

---

## Self-Review

**Spec coverage:** Notes zone (Tasks 1,5,14), Discussion feed (Tasks 5,14), flat (no threading — confirmed), @-mentions+notifications (Tasks 9,13,14), unread badge (Tasks 8,16), reactions (Tasks 7,14), edit/delete own (Tasks 6,14), share-with-agent pull model (Tasks 7,14,18-20), markdown reuse (Task 12), polling (Task 14), new container + infra (Task 2), userSettings read markers (Task 4), agent tool (Tasks 18-20). All spec sections map to tasks.

**Placeholder scan:** No "TBD/TODO" left as work. Two explicit decision-points are flagged for the implementer (test-double constructor seam in Task 5; "since mount" vs marker-based unread in Task 16) — both give a concrete default plus the better option, not a blank.

**Type consistency:** `TaskComment`/`Comment` fields match across backend (Task 1) and frontend (Task 10). `CommentKinds`/`NoteTypes` string values (`note`/`message`, `decision`/`open_question`/`note`) are identical on both sides and in the agent query (Task 18). `api.comments` method names match `TeamTab` call sites. `GetRecentAsync` signature change (Task 9) is propagated to its caller.
