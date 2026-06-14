# Steerable Agent Reasoning — Stage 1: Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the persistence foundations for the steerable agentic loop — a per-task prompt field, a persisted `RunEvent` trace model, a `PendingMessage` steering-inbox model, and the two Cosmos containers (`runEvents`, `pendingMessages`) in both the runtime bootstrap and infra Bicep.

**Architecture:** Pure additive data-layer changes in `TectikaAgents.Core` (models) and `TectikaAgents.Api` (Cosmos container bootstrap), plus an `infra/modules/data.bicep` update. No behavior change yet — later stages build the tool-loop, orchestration, and UI on top. Container creation is driven at runtime by `CosmosDbService.EnsureInfrastructureAsync()` (the source of truth); Bicep is updated in parallel to honor the project's infra-idempotency rule.

**Tech Stack:** C# / .NET 10, xUnit, Azure Cosmos DB (serverless, SQL API), Bicep.

**Spec:** `docs/superpowers/specs/2026-06-14-steerable-agent-reasoning-design.md`

**Conventions to follow (from existing code):**
- Models live in `src/core/TectikaAgents.Core/Models/`, namespace `TectikaAgents.Core.Models`, every property has a `[JsonPropertyName("camelCase")]` attribute, `Id` defaults to `Guid.NewGuid().ToString()`, `DateTimeOffset` timestamps default to `DateTimeOffset.UtcNow`. (See `Artifact.cs`, `AgentTask.cs`.)
- Tests live in `tests/TectikaAgents.Tests/`, xUnit, **no namespace** (top-level), `using Xunit;`. (See `ContextManagerTests.cs`.)
- Test command from repo root: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`

---

## File Structure

- **Create** `src/core/TectikaAgents.Core/Models/RunEvent.cs` — the `RunEvent` document + `RunEventKind` enum (persisted hierarchical activity/chat trace).
- **Create** `src/core/TectikaAgents.Core/Models/PendingMessage.cs` — the `PendingMessage` document (steering inbox).
- **Modify** `src/core/TectikaAgents.Core/Models/AgentTask.cs` — add the `Prompt` field.
- **Modify** `src/api/TectikaAgents.Api/Services/CosmosDbService.cs` — add two container-name constants and a testable static container-definition list used by `EnsureInfrastructureAsync()`.
- **Modify** `infra/modules/data.bicep` — add `runEvents` + `pendingMessages` to the `containers` array.
- **Create** `tests/TectikaAgents.Tests/RunEventTests.cs` — model shape tests.
- **Create** `tests/TectikaAgents.Tests/PendingMessageTests.cs` — model shape tests.
- **Create** `tests/TectikaAgents.Tests/AgentTaskPromptTests.cs` — prompt-field serialization test.
- **Create** `tests/TectikaAgents.Tests/CosmosContainerDefinitionsTests.cs` — asserts the new containers are registered with correct partition keys.

---

### Task 1: Add `Prompt` field to `AgentTask`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/AgentTask.cs:55-59`
- Test: `tests/TectikaAgents.Tests/AgentTaskPromptTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/AgentTaskPromptTests.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentTaskPromptTests
{
    [Fact]
    public void Prompt_SerializesAsCamelCase_AndRoundTrips()
    {
        var task = new AgentTask { Id = "t1", Prompt = "Write the API client for THIS task." };

        var json = JsonSerializer.Serialize(task);
        Assert.Contains("\"prompt\":\"Write the API client for THIS task.\"", json);

        var back = JsonSerializer.Deserialize<AgentTask>(json)!;
        Assert.Equal("Write the API client for THIS task.", back.Prompt);
    }

    [Fact]
    public void Prompt_DefaultsToNull()
    {
        Assert.Null(new AgentTask().Prompt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~AgentTaskPromptTests"`
Expected: FAIL — compile error, `AgentTask` has no property `Prompt`.

- [ ] **Step 3: Add the field**

In `src/core/TectikaAgents.Core/Models/AgentTask.cs`, immediately after the `taskBrief` property (line 56), add:

```csharp
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~AgentTaskPromptTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/AgentTask.cs tests/TectikaAgents.Tests/AgentTaskPromptTests.cs
git commit -m "feat(core): add per-task Prompt field to AgentTask"
```

---

### Task 2: Add `RunEvent` model + `RunEventKind` enum

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/RunEvent.cs`
- Test: `tests/TectikaAgents.Tests/RunEventTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/RunEventTests.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class RunEventTests
{
    [Fact]
    public void RunEvent_HasDefaults()
    {
        var e = new RunEvent();
        Assert.False(string.IsNullOrWhiteSpace(e.Id));          // GUID by default
        Assert.Equal(RunEventKind.Thinking, e.Kind);            // first enum member
        Assert.True(e.Timestamp <= DateTimeOffset.UtcNow);
        Assert.Null(e.ParentId);                                // round-level by default
    }

    [Fact]
    public void RunEvent_SerializesCamelCase()
    {
        var e = new RunEvent
        {
            Id = "ev1", TaskId = "t1", RunId = "r1", Round = 2,
            ParentId = "ev0", Kind = RunEventKind.ToolCall,
            Title = "Gathering data about the project",
            ToolName = "GetArtifact", ToolArgsSummary = "taskId=u1",
            ResultSummary = "1.2 KB markdown"
        };

        var json = JsonSerializer.Serialize(e);

        Assert.Contains("\"taskId\":\"t1\"", json);
        Assert.Contains("\"parentId\":\"ev0\"", json);
        Assert.Contains("\"round\":2", json);
        Assert.Contains("\"toolName\":\"GetArtifact\"", json);
        Assert.Contains("\"kind\":\"ToolCall\"", json);          // enum serialized as string name

        var back = JsonSerializer.Deserialize<RunEvent>(json)!;
        Assert.Equal(RunEventKind.ToolCall, back.Kind);
        Assert.Equal("Gathering data about the project", back.Title);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~RunEventTests"`
Expected: FAIL — `RunEvent` / `RunEventKind` do not exist.

- [ ] **Step 3: Create the model**

Create `src/core/TectikaAgents.Core/Models/RunEvent.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// One persisted entry in a task's run trace. Single source of truth for both the
/// Activity tab (hierarchical timeline) and the chat transcript. Stored in the
/// `runEvents` container, partitioned by `taskId`. The same shape is broadcast over SSE,
/// so live and stored events are identical by construction.
/// </summary>
public class RunEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>Round index within the run. Parent activities and their sub-activities share a round.</summary>
    [JsonPropertyName("round")]
    public int Round { get; set; }

    /// <summary>Null = round-level activity (a parent); set = a sub-activity nested under a round event.</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("kind")]
    public RunEventKind Kind { get; set; } = RunEventKind.Thinking;

    /// <summary>Human headline, e.g. "Gathering data about the project" (round_started) or the agent/user text.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [JsonPropertyName("toolArgsSummary")]
    public string? ToolArgsSummary { get; set; }

    [JsonPropertyName("resultSummary")]
    public string? ResultSummary { get; set; }

    [JsonPropertyName("tokenUsage")]
    public TokenUsage? TokenUsage { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunEventKind
{
    Thinking,
    RoundStarted,
    ToolCall,
    ToolResult,
    ArtifactWritten,
    UserMessage,
    AgentMessage,
    InteractionRequired,
    ApprovalRequired,
    RoundCompleted,
    RunCompleted,
    RunFailed
}
```

> **Note:** `TokenUsage` already exists in `TectikaAgents.Core.Models` (used by `Artifact`/`AgentRunOutcome`); reuse it, do not redefine. If a compile error says `TokenUsage` is ambiguous or missing, confirm its namespace with `grep -rn "class TokenUsage\|record TokenUsage" src/core` and adjust the `using` — do not create a second one.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~RunEventTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/RunEvent.cs tests/TectikaAgents.Tests/RunEventTests.cs
git commit -m "feat(core): add RunEvent persisted trace model"
```

---

### Task 3: Add `PendingMessage` model

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/PendingMessage.cs`
- Test: `tests/TectikaAgents.Tests/PendingMessageTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/PendingMessageTests.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class PendingMessageTests
{
    [Fact]
    public void PendingMessage_HasDefaults()
    {
        var m = new PendingMessage();
        Assert.False(string.IsNullOrWhiteSpace(m.Id));
        Assert.False(m.Consumed);
        Assert.True(m.CreatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void PendingMessage_SerializesCamelCase()
    {
        var m = new PendingMessage { Id = "m1", RunId = "r1", TaskId = "t1", Text = "use the cheaper hotel", Consumed = true };
        var json = JsonSerializer.Serialize(m);
        Assert.Contains("\"runId\":\"r1\"", json);
        Assert.Contains("\"text\":\"use the cheaper hotel\"", json);
        Assert.Contains("\"consumed\":true", json);

        var back = JsonSerializer.Deserialize<PendingMessage>(json)!;
        Assert.Equal("t1", back.TaskId);
        Assert.True(back.Consumed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PendingMessageTests"`
Expected: FAIL — `PendingMessage` does not exist.

- [ ] **Step 3: Create the model**

Create `src/core/TectikaAgents.Core/Models/PendingMessage.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// A user steering message queued for a running task. The chat API raises a Durable external
/// event AND records one of these so the orchestrator loop drains it deterministically across
/// replay. Stored in the `pendingMessages` container, partitioned by `runId`.
/// </summary>
public class PendingMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("consumed")]
    public bool Consumed { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PendingMessageTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/PendingMessage.cs tests/TectikaAgents.Tests/PendingMessageTests.cs
git commit -m "feat(core): add PendingMessage steering-inbox model"
```

---

### Task 4: Register the two new Cosmos containers (runtime bootstrap)

Refactor `EnsureInfrastructureAsync` to use a testable static container-definition list, then add the two new containers.

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs:17-26` (constants) and `:39-60` (bootstrap)
- Test: `tests/TectikaAgents.Tests/CosmosContainerDefinitionsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/CosmosContainerDefinitionsTests.cs`:

```csharp
using TectikaAgents.Api.Services;
using Xunit;

public class CosmosContainerDefinitionsTests
{
    [Fact]
    public void Definitions_IncludeRunEvents_PartitionedByTaskId()
    {
        Assert.Contains(
            CosmosDbService.ContainerDefinitions,
            d => d.Name == "runEvents" && d.PartitionKey == "/taskId");
    }

    [Fact]
    public void Definitions_IncludePendingMessages_PartitionedByRunId()
    {
        Assert.Contains(
            CosmosDbService.ContainerDefinitions,
            d => d.Name == "pendingMessages" && d.PartitionKey == "/runId");
    }

    [Fact]
    public void Definitions_StillIncludeExistingCoreContainers()
    {
        var names = System.Linq.Enumerable.ToHashSet(
            System.Linq.Enumerable.Select(CosmosDbService.ContainerDefinitions, d => d.Name));
        Assert.Contains("tasks", names);
        Assert.Contains("artifacts", names);
        Assert.Contains("taskEdges", names);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~CosmosContainerDefinitionsTests"`
Expected: FAIL — `CosmosDbService.ContainerDefinitions` does not exist.

- [ ] **Step 3: Add constants + the static definition list, and consume it in the bootstrap**

In `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`, after the existing container-name constants (after line 26, `TaskEdgesContainer`), add:

```csharp
    public const string RunEventsContainer = "runEvents";
    public const string PendingMessagesContainer = "pendingMessages";

    /// <summary>Authoritative list of Cosmos containers this app requires (name + partition key).
    /// Source of truth for <see cref="EnsureInfrastructureAsync"/> and kept in sync with infra/modules/data.bicep.</summary>
    public static readonly (string Name, string PartitionKey)[] ContainerDefinitions =
    {
        (BoardsContainer,            "/tenantId"),
        (TasksContainer,             "/boardId"),
        (AgentRolesContainer,        "/tenantId"),
        (WorkflowRunsContainer,      "/taskId"),
        (ArtifactsContainer,         "/taskId"),
        (ApprovalsContainer,         "/runId"),
        (AuditLogContainer,          "/tenantId"),
        (HumanInteractionsContainer, "/runId"),
        (TaskEdgesContainer,         "/boardId"),
        (RunEventsContainer,         "/taskId"),
        (PendingMessagesContainer,   "/runId"),
    };
```

Then replace the body of `EnsureInfrastructureAsync` (the inline `containers` array + loop, lines 43-57) so it iterates the shared list:

```csharp
    public async Task EnsureInfrastructureAsync()
    {
        var db = await _client.CreateDatabaseIfNotExistsAsync(_dbName);

        foreach (var (name, pk) in ContainerDefinitions)
            await db.Database.CreateContainerIfNotExistsAsync(name, pk);

        _logger.LogInformation("[CosmosInfra] ensured database {Database} and {Count} containers", _dbName, ContainerDefinitions.Length);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~CosmosContainerDefinitionsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/CosmosDbService.cs tests/TectikaAgents.Tests/CosmosContainerDefinitionsTests.cs
git commit -m "feat(api): register runEvents + pendingMessages Cosmos containers"
```

---

### Task 5: Add the two containers to infra Bicep

Honors the infra-idempotency rule: a fresh-tenant deploy provisions the new containers.

**Files:**
- Modify: `infra/modules/data.bicep:36-42`

- [ ] **Step 1: Edit the `containers` array**

In `infra/modules/data.bicep`, replace the `containers` array (lines 36-42) with the full set the app uses, including the two new ones. (This also aligns the Bicep names with the runtime source of truth for the containers this feature touches; pre-existing unrelated drift is intentionally left for a separate cleanup.)

```bicep
// Partition keys are immutable and must match the live containers (the running app,
// CosmosDbService.ContainerDefinitions, is the source of truth). tasks is partitioned by /boardId.
var containers = [
  { name: 'boards', pk: '/tenantId' }
  { name: 'tasks', pk: '/boardId' }
  { name: 'agentRoles', pk: '/tenantId' }
  { name: 'workflowRuns', pk: '/taskId' }
  { name: 'artifacts', pk: '/taskId' }
  { name: 'approvals', pk: '/runId' }
  { name: 'auditLog', pk: '/tenantId' }
  { name: 'humanInteractions', pk: '/runId' }
  { name: 'taskEdges', pk: '/boardId' }
  { name: 'runEvents', pk: '/taskId' }
  { name: 'pendingMessages', pk: '/runId' }
]
```

> **Why replace the whole list:** the prior array (`tasks`/`agents`/`runs`/`approvals`/`audit`) did not match the app's real containers, so a from-scratch deploy never created `artifacts`, `boards`, `taskEdges`, etc. The app creates them at runtime via `EnsureInfrastructureAsync`, but the idempotency rule wants Bicep complete too. Names/PKs above mirror `CosmosDbService.ContainerDefinitions` exactly.

- [ ] **Step 2: Validate the Bicep compiles (if tooling present)**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo "BICEP_OK"`
Expected: prints `BICEP_OK` with no errors.
If `az`/`bicep` is not installed in this environment, skip — verify instead that the array is valid Bicep object syntax (each entry on its own line, no commas between objects, matching the existing style).

- [ ] **Step 3: Commit**

```bash
git add infra/modules/data.bicep
git commit -m "infra(data): add runEvents + pendingMessages containers; align list with app"
```

---

### Task 6: Full build + test sweep

**Files:** none (verification only)

- [ ] **Step 1: Build the solution**

Run: `dotnet build TectikaAgents.slnx`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 2: Run the whole test suite**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: all tests pass (existing + the 4 new test classes from this stage). No failures.

- [ ] **Step 3: Confirm no stray uncommitted changes**

Run: `git status -s`
Expected: clean (everything committed in Tasks 1-5). If anything remains, review and commit it with an appropriate message.

---

## Self-Review

**1. Spec coverage (Stage 1 scope = data model + infra):**
- `AgentTask.Prompt` → Task 1. ✓
- `RunEvent` container/model (incl. all `Kind` values from spec) → Task 2. ✓
- `PendingMessage` model → Task 3. ✓
- `runEvents` (`/taskId`) + `pendingMessages` (`/runId`) container creation → Task 4 (runtime) + Task 5 (infra). ✓
- Infra-idempotency rule honored → Task 5. ✓
- Deferred to later stages (correctly NOT in this plan): `ContextManager`/`IProjectExplorer`/tool-loop (Stage 2), orchestration + chat API + `RunEvent` writes/reads + `AgentEvent` extensions (Stage 3), UI (Stage 4). The `WorkflowCosmosService` read/write methods for `RunEvent`/`PendingMessage` belong to Stage 3 and are intentionally absent here.

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step shows complete content. ✓

**3. Type consistency:** `RunEventKind` member names (`ToolCall`, `RoundStarted`, …) are used consistently in Task 2 test + model. Container constants (`RunEventsContainer="runEvents"`, `PendingMessagesContainer="pendingMessages"`) and partition keys (`/taskId`, `/runId`) match across Task 4 (code), Task 4 test, and Task 5 (Bicep). `TokenUsage` reused, not redefined (note in Task 2). ✓
