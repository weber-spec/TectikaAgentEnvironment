# Steerable Agent Reasoning — Stage 2: Reasoning Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Give agents a real, board-scoped, multi-round tool-loop: bake the Tectika tool schema into every Foundry agent definition, add board exploration tools, make context smart-budgeted + per-task-prompt-aware, and implement an offline-testable agentic loop. Finish with a live smoke test against `proj-agentteam`.

**Architecture:** Tools are attached to the agent **definition** (verified Foundry contract — per-request tools are rejected in `agent_reference` mode; see memory `foundry-tool-calling-verified`). The loop is a standalone `AgentToolLoop` driven by a `sendRound` delegate (abstracts Foundry HTTP) and an `IProjectExplorer` (abstracts the board), so it unit-tests with no Azure. `FoundryAgentRuntime` wires real HTTP + a real explorer into the loop; `MockAgentRuntime` scripts it.

**Tech Stack:** C# / .NET 10, xUnit, Azure AI Foundry `/openai/v1/responses` (agent_reference + conversation + `function_call`/`function_call_output`).

**Spec:** `docs/superpowers/specs/2026-06-14-steerable-agent-reasoning-design.md`
**Depends on:** Stage 1 (merged): `AgentTask.Prompt`, `RunEvent`, `PendingMessage`, containers.

**Verified Foundry contract (do not deviate):**
- Agent definition: `{ kind:"prompt", model, instructions, tools:[ {type:"function", name, description, parameters} ] }` (flat function shape; nested `{function:{…}}` is 400).
- Run: `POST {base}/openai/v1/responses { input, agent_reference:{name,type:"agent_reference"}, conversation }` → output items; a tool turn yields `{type:"function_call", name, arguments(JSON string), call_id}`.
- Continue: next call `input:[{type:"function_call_output", call_id, output}]` + same `conversation` → final `{type:"message", content:[{type:"output_text", text}]}`.

**Conventions:** models/interfaces in `TectikaAgents.Core` (`[JsonPropertyName]` camelCase); runtime code in `src/agentruntime` (namespace `TectikaAgents.AgentRuntime`); board data access in `src/workflows/Services`; tests in `tests/TectikaAgents.Tests` (xUnit, no namespace). Test cmd: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~<Class>"`.

---

## File Structure

- **Create** `src/agentruntime/TectikaToolSchema.cs` — declarative catalog of explore+control tools + `Version` + Foundry-shape serializer.
- **Modify** `src/agentruntime/AgentInstructionsHash.cs` — include `toolsVersion` in the hash.
- **Modify** `src/agentruntime/FoundryAgentRuntime.cs` — attach tools to the definition; new hash; run the loop in `RunTurnAsync`.
- **Modify** `src/agentruntime/MockAgentRuntime.cs` — new hash arg; scriptable tool-loop.
- **Create** `src/agentruntime/AgentToolLoop.cs` — HTTP-free multi-round loop (the testable core).
- **Create** `src/core/TectikaAgents.Core/Interfaces/IProjectExplorer.cs` — board-scoped explore interface + result DTOs.
- **Create** `src/workflows/Services/BoardProjectExplorer.cs` — `IProjectExplorer` over `WorkflowCosmosService`.
- **Modify** `src/workflows/Services/ContextManager.cs` — smart-budgeted floor + per-task prompt + exploration nudge; drop embedded protocol prose (now tools).
- **Modify** `src/core/TectikaAgents.Core/Models/AgentRunContracts.cs` — enrich `AgentRunOutcome` with `RoundIntent` + `PendingControl`.
- **Tests:** `TectikaToolSchemaTests`, `AgentInstructionsHashToolsTests`, `AgentToolLoopTests`, `ContextManagerBudgetTests`, plus a live smoke `tests/live/foundry_loop_smoke.py`.

---

### Task 1: Tool schema catalog (`TectikaToolSchema`)

**Files:** Create `src/agentruntime/TectikaToolSchema.cs`; Test `tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using TectikaAgents.AgentRuntime;
using Xunit;

public class TectikaToolSchemaTests
{
    [Fact]
    public void Catalog_ContainsExploreAndControlTools()
    {
        var names = TectikaToolSchema.Definitions.Select(d => d.Name).ToHashSet();
        foreach (var n in new[] { "get_board_overview", "search_tasks", "get_task", "get_artifact",
                                  "request_human_input", "request_approval", "request_revision",
                                  "update_brief", "round_intent" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void FoundryShape_IsFlatFunction()
    {
        var json = TectikaToolSchema.ToFoundryToolsJson();      // returns a JsonArray-serializable object
        var doc = JsonSerializer.SerializeToElement(json);
        var first = doc[0];
        Assert.Equal("function", first.GetProperty("type").GetString());
        Assert.True(first.TryGetProperty("name", out _));        // flat: name at top level
        Assert.True(first.TryGetProperty("parameters", out _));
        Assert.False(first.TryGetProperty("function", out _));   // NOT nested
    }

    [Fact]
    public void Version_IsStableNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(TectikaToolSchema.Version));
        Assert.Equal(TectikaToolSchema.Version, TectikaToolSchema.Version);
    }
}
```

- [ ] **Step 2: Run → FAIL** (`TectikaToolSchema` missing).
Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~TectikaToolSchemaTests"`

- [ ] **Step 3: Implement** `src/agentruntime/TectikaToolSchema.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.AgentRuntime;

/// <summary>Declarative catalog of the fixed Tectika agent tools. Attached to EVERY Foundry agent
/// definition (Foundry rejects per-request tools in agent_reference mode). Bump <see cref="Version"/>
/// whenever the toolset changes so AgentInstructionsHash republishes agent versions.</summary>
public static class TectikaToolSchema
{
    public const string Version = "tools-v1";

    public sealed record ToolProp(string Type, string? Description = null, string[]? Enum = null);
    public sealed record ToolDef(
        string Name, string Description,
        IReadOnlyDictionary<string, ToolProp> Properties, string[] Required);

    public static readonly IReadOnlyList<ToolDef> Definitions = new ToolDef[]
    {
        // ── Explore (board-scoped, read-only) ──────────────────────────────────
        new("get_board_overview", "List every task on this board with id, title, status, assignee, and dependency edges. Use this first to understand the whole project.",
            new Dictionary<string, ToolProp>(), []),
        new("search_tasks", "Find tasks on this board whose title/description/brief match a query.",
            new Dictionary<string, ToolProp> { ["query"] = new("string", "Free-text search terms.") }, ["query"]),
        new("get_task", "Get one task's details (title, description, status, brief, current artifact summary).",
            new Dictionary<string, ToolProp> { ["taskId"] = new("string", "The task id.") }, ["taskId"]),
        new("get_artifact", "Get the full artifact content produced by a task (latest version unless one is given).",
            new Dictionary<string, ToolProp> {
                ["taskId"] = new("string", "The task whose artifact to read."),
                ["version"] = new("integer", "Optional specific version; omit for latest.") }, ["taskId"]),
        // ── Control (signal the orchestrator) ──────────────────────────────────
        new("round_intent", "Announce, in one short line, what you are about to do this round. Call this at the START of each round.",
            new Dictionary<string, ToolProp> { ["text"] = new("string", "One-line intent, e.g. 'Gathering data about the project'.") }, ["text"]),
        new("update_brief", "Append a one-line note to this task's running brief (history visible to downstream tasks).",
            new Dictionary<string, ToolProp> { ["text"] = new("string", "One-line brief update.") }, ["text"]),
        new("request_human_input", "Pause and ask the human a question (free-text, or multiple-choice if options given). Only when you genuinely cannot proceed.",
            new Dictionary<string, ToolProp> {
                ["question"] = new("string", "The question to ask."),
                ["options"] = new("array", "Optional choices the user picks from.") }, ["question"]),
        new("request_approval", "Pause and ask the human to approve/reject before continuing.",
            new Dictionary<string, ToolProp> { ["description"] = new("string", "What needs approval.") }, ["description"]),
        new("request_revision", "(QA/validator agents) Signal that an upstream task must be re-run with fixes.",
            new Dictionary<string, ToolProp> { ["reason"] = new("string", "What must be fixed.") }, ["reason"]),
    };

    /// <summary>Project the catalog into the Foundry flat function-tool array (definition.tools).</summary>
    public static IReadOnlyList<object> ToFoundryToolsJson() =>
        Definitions.Select(d => (object)new FoundryTool(
            "function", d.Name, d.Description,
            new FoundryParams("object",
                d.Properties.ToDictionary(p => p.Key, p => new FoundryProp(p.Value.Type, p.Value.Description, p.Value.Enum)),
                d.Required))).ToList();

    // Foundry wire shapes (flat function tool).
    private sealed record FoundryTool(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] FoundryParams Parameters);
    private sealed record FoundryParams(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, FoundryProp> Properties,
        [property: JsonPropertyName("required")] string[] Required);
    private sealed record FoundryProp(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("enum")] string[]? Enum);
}
```

- [ ] **Step 4: Run → PASS** (3 tests). Same command as Step 2.

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/TectikaToolSchema.cs tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs
git commit -m "feat(runtime): add Tectika tool schema catalog + Foundry flat-shape serializer"
```

---

### Task 2: Version the agent hash by toolset

**Files:** Modify `src/agentruntime/AgentInstructionsHash.cs`; Test `tests/TectikaAgents.Tests/AgentInstructionsHashToolsTests.cs`.

- [ ] **Step 1: Failing test**

```csharp
using TectikaAgents.AgentRuntime;
using Xunit;

public class AgentInstructionsHashToolsTests
{
    [Fact]
    public void Hash_ChangesWithToolsVersion()
    {
        var a = AgentInstructionsHash.Compute("prompt", "gpt-4o", "tools-v1");
        var b = AgentInstructionsHash.Compute("prompt", "gpt-4o", "tools-v2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_StableForSameInputs()
    {
        Assert.Equal(
            AgentInstructionsHash.Compute("p", "m", "tools-v1"),
            AgentInstructionsHash.Compute("p", "m", "tools-v1"));
    }
}
```

- [ ] **Step 2: Run → FAIL** (existing `Compute` has 2 params; 3-arg overload missing — compile error).

- [ ] **Step 3: Implement** — replace the method body in `AgentInstructionsHash.cs`:

```csharp
    public static string Compute(string systemPrompt, string model, string toolsVersion)
    {
        var bytes = Encoding.UTF8.GetBytes($"{model}\n{toolsVersion}\n \n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
```

> This **replaces** the 2-arg method. Update its two callers in the next tasks (FoundryAgentRuntime, MockAgentProvisioner). Do NOT keep the 2-arg overload — a single signature prevents drift.

- [ ] **Step 4: Run → PASS** (2 tests). The solution won't fully build until Task 3/4 update callers; that's expected — run this filter, which only needs the Core+runtime compile of this file. If the runtime project fails to build due to the stale callers, do Task 3 Step 3 and Task 4 Step 3 first, then run all three filters together.

- [ ] **Step 5: Commit** (with caller updates from Tasks 3-4 if build coupling requires; otherwise alone)

```bash
git add src/agentruntime/AgentInstructionsHash.cs tests/TectikaAgents.Tests/AgentInstructionsHashToolsTests.cs
git commit -m "feat(runtime): version agent instructions hash by tool-schema version"
```

---

### Task 3: Attach tools to the Foundry agent definition

**Files:** Modify `src/agentruntime/FoundryAgentRuntime.cs` (`AgentDefinition` record ~218-222; `EnsureAgentAsync` ~69-113).

- [ ] **Step 1: Add `tools` to the definition record.** Replace the `AgentDefinition` record with:

```csharp
    private sealed record AgentDefinition(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("instructions")] string Instructions,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("tools")] IReadOnlyList<object> Tools);
```

- [ ] **Step 2: Build the definition with tools + new hash.** In `EnsureAgentAsync`, replace the `hash` and `definition` lines:

```csharp
            var hash = AgentInstructionsHash.Compute(role.SystemPrompt, model, TectikaToolSchema.Version);
            var definition = new AgentDefinition("prompt", model, role.SystemPrompt, role.DisplayName,
                TectikaToolSchema.ToFoundryToolsJson());
```

- [ ] **Step 3: Verify it still compiles + existing tests pass.**
Run: `dotnet build TectikaAgents.slnx` → 0 errors. (No new unit test here; covered by the live smoke in Task 8 and by `TectikaToolSchemaTests`.)

- [ ] **Step 4: Commit**

```bash
git add src/agentruntime/FoundryAgentRuntime.cs
git commit -m "feat(runtime): attach Tectika tool schema to every Foundry agent definition"
```

---

### Task 4: Update MockAgentProvisioner to the new hash

**Files:** Modify `src/agentruntime/MockAgentRuntime.cs` (`MockAgentProvisioner.EnsureAgentAsync`).

- [ ] **Step 1: Update the hash call.** Replace the `role.FoundryAgentHash = ...` line:

```csharp
        role.FoundryAgentHash = AgentInstructionsHash.Compute(role.SystemPrompt, role.ModelOverride ?? "mock", TectikaToolSchema.Version);
```

- [ ] **Step 2: Build + run existing mock tests.**
Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~MockAgentRuntimeTests"` → PASS.

- [ ] **Step 3: Commit**

```bash
git add src/agentruntime/MockAgentRuntime.cs
git commit -m "fix(runtime): mock provisioner uses tool-versioned instructions hash"
```

---

### Task 5: `IProjectExplorer` interface + result DTOs

**Files:** Create `src/core/TectikaAgents.Core/Interfaces/IProjectExplorer.cs`.

- [ ] **Step 1: Create the interface (no test — it's a contract; exercised via the loop in Task 7).**

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Board-scoped, read-only project exploration for agent tools. An instance is bound to a
/// single board at construction; agents cannot reach other boards.</summary>
public interface IProjectExplorer
{
    Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string query, CancellationToken ct = default);
    Task<TaskDetail?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<ArtifactView?> GetArtifactAsync(string taskId, int? version, CancellationToken ct = default);
}

public sealed record BoardOverview(string BoardId, string BoardName, IReadOnlyList<TaskNode> Tasks);
public sealed record TaskNode(string Id, string Title, string Status, string AssigneeId, IReadOnlyList<string> DependsOn);
public sealed record TaskSummary(string Id, string Title, string Status, string? ArtifactSummary);
public sealed record TaskDetail(string Id, string Title, string Description, string Status, string TaskBrief, string? ArtifactSummary);
public sealed record ArtifactView(string TaskId, int Version, string ContentType, string Content);
```

- [ ] **Step 2: Build** → `dotnet build TectikaAgents.slnx` → 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Interfaces/IProjectExplorer.cs
git commit -m "feat(core): add board-scoped IProjectExplorer contract + DTOs"
```

---

### Task 6: `BoardProjectExplorer` over `WorkflowCosmosService`

**Files:** Create `src/workflows/Services/BoardProjectExplorer.cs`. May add read helpers to `WorkflowCosmosService.cs`.

> Note: `BoardProjectExplorer` is a thin adapter over `WorkflowCosmosService` (which talks to live Cosmos), so it is covered by the Task 8 live smoke rather than an isolated unit test. Keep it free of logic beyond mapping.

- [ ] **Step 1: Implement the adapter.**

```csharp
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>IProjectExplorer bound to one board, backed by WorkflowCosmosService.</summary>
public sealed class BoardProjectExplorer : IProjectExplorer
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly string _boardId;
    private readonly string _tenantId;

    public BoardProjectExplorer(WorkflowCosmosService cosmos, string boardId, string tenantId)
    {
        _cosmos = cosmos; _boardId = boardId; _tenantId = tenantId;
    }

    public async Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
    {
        var board = await _cosmos.GetBoardAsync(_boardId, _tenantId, ct);
        var tasks = await _cosmos.GetBoardTasksAsync(_boardId, ct);
        var nodes = new List<TaskNode>();
        foreach (var t in tasks)
        {
            var deps = await _cosmos.GetUpstreamTaskIdsAsync(_boardId, t.Id, ct);
            nodes.Add(new TaskNode(t.Id, t.Title, t.Status.ToString(), t.Assignee.Id, deps));
        }
        return new BoardOverview(_boardId, board?.Name ?? _boardId, nodes);
    }

    public async Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        var tasks = await _cosmos.GetBoardTasksAsync(_boardId, ct);
        return tasks
            .Where(t => q.Length == 0
                || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (t.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (t.TaskBrief ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(t => new TaskSummary(t.Id, t.Title, t.Status.ToString(), t.ArtifactSummary))
            .ToList();
    }

    public async Task<TaskDetail?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        var t = await _cosmos.GetTaskAsync(_boardId, taskId, ct);
        return t is null ? null
            : new TaskDetail(t.Id, t.Title, t.Description, t.Status.ToString(), t.TaskBrief, t.ArtifactSummary);
    }

    public async Task<ArtifactView?> GetArtifactAsync(string taskId, int? version, CancellationToken ct = default)
    {
        var arts = await _cosmos.GetUpstreamArtifactsAsync([taskId], ct); // latest per task
        var art = version is null ? arts.FirstOrDefault() : arts.FirstOrDefault(a => a.Version == version);
        return art is null ? null
            : new ArtifactView(art.TaskId, art.Version, art.ContentType.ToString(), art.Content);
    }
}
```

> `GetBoardTasksAsync` currently projects only `id,title,status,taskBrief` (see WorkflowCosmosService:248). For overview/search we also need `assignee`, `description`, `artifactSummary`. **Widen that SELECT** to `SELECT * FROM c WHERE c.boardId=@boardId` (or add the missing fields) so the mapping above has data. Make that edit in this task.

- [ ] **Step 2: Build** → 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/workflows/Services/BoardProjectExplorer.cs src/workflows/Services/WorkflowCosmosService.cs
git commit -m "feat(workflows): BoardProjectExplorer adapter over Cosmos for agent tools"
```

---

### Task 7: The offline-testable `AgentToolLoop`

This is the heart. The loop is HTTP-free: a `SendRound` delegate returns either tool calls or final text; the loop executes explore tools via `IProjectExplorer`, records control tools, and submits outputs. `FoundryAgentRuntime` provides a real `SendRound`; tests provide a fake.

**Files:** Create `src/agentruntime/AgentToolLoop.cs`; enrich `AgentRunOutcome` (`AgentRunContracts.cs`); Test `tests/TectikaAgents.Tests/AgentToolLoopTests.cs`.

- [ ] **Step 1: Enrich `AgentRunOutcome`.** In `src/core/TectikaAgents.Core/Models/AgentRunContracts.cs`, replace the `AgentRunOutcome` record with:

```csharp
public sealed record AgentRunOutcome(
    AgentRunStatus Status,
    string Content,
    ArtifactContentType ContentType,
    TokenUsage TokenUsage,
    string CompletionId,
    string? BriefUpdate = null,
    string? Error = null,
    string? RoundIntent = null,
    PendingControl? Control = null);

/// <summary>A control-tool the agent invoked that the orchestrator must act on.</summary>
public sealed record PendingControl(PendingControlKind Kind, string Text, IReadOnlyList<string>? Options = null);
public enum PendingControlKind { HumanInput, Approval, Revision }
```

- [ ] **Step 2: Failing test** `tests/TectikaAgents.Tests/AgentToolLoopTests.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentToolLoopTests
{
    // Minimal fake explorer: get_board_overview returns one task; get_artifact returns content.
    private sealed class FakeExplorer : IProjectExplorer
    {
        public int OverviewCalls;
        public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
        { OverviewCalls++; return Task.FromResult(new BoardOverview("b","Board",
            new[]{ new TaskNode("u1","Upstream","Done","agent-x", Array.Empty<string>()) })); }
        public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string q, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<TaskSummary>)Array.Empty<TaskSummary>());
        public Task<TaskDetail?> GetTaskAsync(string id, CancellationToken ct = default)
            => Task.FromResult<TaskDetail?>(null);
        public Task<ArtifactView?> GetArtifactAsync(string id, int? v, CancellationToken ct = default)
            => Task.FromResult<ArtifactView?>(new ArtifactView(id, 1, "Markdown", "UPSTREAM-CONTENT"));
    }

    private static ToolCall FC(string name, object args) =>
        new(name, JsonSerializer.Serialize(args), $"call_{name}");

    [Fact]
    public async Task Loop_ExecutesExploreTool_ThenReturnsFinalText()
    {
        var explorer = new FakeExplorer();
        var rounds = new Queue<RoundResponse>(new[]
        {
            // round 1: ask for the board overview
            RoundResponse.Tools(new[]{ FC("get_board_overview", new {}) }),
            // round 2: produce final answer (loop ends)
            RoundResponse.Final("# Done\n\nUsed the board.", new TokenUsage{ Input=10, Output=5 }),
        });

        var loop = new AgentToolLoop(explorer);
        var toolEvents = new List<string>();
        var result = await loop.RunAsync(
            sendRound: (inputs, ct) => Task.FromResult(rounds.Dequeue()),
            maxRounds: 8,
            onToolCall: (name, args) => toolEvents.Add(name),
            ct: default);

        Assert.Equal(1, explorer.OverviewCalls);
        Assert.Contains("get_board_overview", toolEvents);
        Assert.Equal("# Done\n\nUsed the board.", result.FinalText);
        Assert.Null(result.Control);
    }

    [Fact]
    public async Task Loop_CapturesControlTools_AndStops()
    {
        var rounds = new Queue<RoundResponse>(new[]
        {
            RoundResponse.Tools(new[]{ FC("round_intent", new { text = "Checking budget" }),
                                       FC("request_human_input", new { question = "Which hotel?", options = new[]{"A","B"} }) }),
        });
        var loop = new AgentToolLoop(new FakeExplorer());
        var result = await loop.RunAsync((i,c)=>Task.FromResult(rounds.Dequeue()), 8, (_,__)=>{}, default);

        Assert.Equal("Checking budget", result.RoundIntent);
        Assert.NotNull(result.Control);
        Assert.Equal(PendingControlKind.HumanInput, result.Control!.Kind);
        Assert.Equal("Which hotel?", result.Control.Text);
        Assert.Equal(new[]{"A","B"}, result.Control.Options);
    }

    [Fact]
    public async Task Loop_StopsAtMaxRounds()
    {
        // Always returns a tool call → never finishes on its own.
        var loop = new AgentToolLoop(new FakeExplorer());
        var result = await loop.RunAsync(
            (i,c)=>Task.FromResult(RoundResponse.Tools(new[]{ FC("get_board_overview", new {}) })),
            maxRounds: 3, onToolCall:(_,__)=>{}, ct: default);
        Assert.True(result.MaxRoundsHit);
        Assert.Equal(3, result.Rounds);
    }
}
```

- [ ] **Step 3: Run → FAIL** (`AgentToolLoop`, `ToolCall`, `RoundResponse` missing).

- [ ] **Step 4: Implement** `src/agentruntime/AgentToolLoop.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>One tool call the model requested.</summary>
public sealed record ToolCall(string Name, string ArgumentsJson, string CallId);

/// <summary>What one Foundry round returned: either tool calls, or final text.</summary>
public sealed class RoundResponse
{
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? FinalText { get; init; }
    public TokenUsage Usage { get; init; } = new();
    public static RoundResponse Tools(IReadOnlyList<ToolCall> calls) => new() { ToolCalls = calls };
    public static RoundResponse Final(string text, TokenUsage usage) => new() { FinalText = text, Usage = usage };
}

/// <summary>One executed tool's output to submit back to the model.</summary>
public sealed record ToolOutput(string CallId, string Output);

public sealed class LoopResult
{
    public string FinalText { get; set; } = "";
    public string? RoundIntent { get; set; }
    public string? BriefUpdate { get; set; }
    public PendingControl? Control { get; set; }
    public TokenUsage Usage { get; set; } = new();
    public int Rounds { get; set; }
    public bool MaxRoundsHit { get; set; }
}

/// <summary>HTTP-free agentic loop. Drives rounds via a delegate; executes explore tools against a
/// board-scoped IProjectExplorer; records control tools for the orchestrator. No Azure dependency.</summary>
public sealed class AgentToolLoop
{
    private readonly IProjectExplorer _explorer;
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public AgentToolLoop(IProjectExplorer explorer) => _explorer = explorer;

    public delegate Task<RoundResponse> SendRound(IReadOnlyList<ToolOutput> toolOutputs, CancellationToken ct);

    public async Task<LoopResult> RunAsync(SendRound sendRound, int maxRounds,
        Action<string, string> onToolCall, CancellationToken ct)
    {
        var result = new LoopResult();
        IReadOnlyList<ToolOutput> pending = Array.Empty<ToolOutput>();

        for (var round = 0; round < maxRounds; round++)
        {
            var resp = await sendRound(pending, ct);
            result.Rounds = round + 1;
            result.Usage = new TokenUsage {
                Input = result.Usage.Input + resp.Usage.Input,
                Output = result.Usage.Output + resp.Usage.Output };

            if (resp.ToolCalls is null || resp.ToolCalls.Count == 0)
            {
                result.FinalText = resp.FinalText ?? "";
                return result;
            }

            var outputs = new List<ToolOutput>();
            foreach (var call in resp.ToolCalls)
            {
                onToolCall(call.Name, call.ArgumentsJson);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                var args = doc.RootElement;

                switch (call.Name)
                {
                    case "round_intent":
                        result.RoundIntent = Str(args, "text");
                        outputs.Add(new(call.CallId, "ok")); break;
                    case "update_brief":
                        result.BriefUpdate = Str(args, "text");
                        outputs.Add(new(call.CallId, "ok")); break;
                    case "request_human_input":
                        result.Control = new(PendingControlKind.HumanInput, Str(args, "question"), StrArr(args, "options"));
                        return result;                       // pause: orchestrator takes over
                    case "request_approval":
                        result.Control = new(PendingControlKind.Approval, Str(args, "description"));
                        return result;
                    case "request_revision":
                        result.Control = new(PendingControlKind.Revision, Str(args, "reason"));
                        return result;
                    case "get_board_overview":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.GetBoardOverviewAsync(ct)))); break;
                    case "search_tasks":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.SearchTasksAsync(Str(args, "query"), ct)))); break;
                    case "get_task":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.GetTaskAsync(Str(args, "taskId"), ct)))); break;
                    case "get_artifact":
                        outputs.Add(new(call.CallId, JsonSerializer.Serialize(await _explorer.GetArtifactAsync(
                            Str(args, "taskId"), IntOrNull(args, "version"), ct)))); break;
                    default:
                        outputs.Add(new(call.CallId, $"error: unknown tool '{call.Name}'")); break;
                }
            }
            pending = outputs;
        }
        result.MaxRoundsHit = true;
        return result;
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static int? IntOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static IReadOnlyList<string>? StrArr(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}
```

- [ ] **Step 5: Run → PASS** (3 tests).
Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~AgentToolLoopTests"`

- [ ] **Step 6: Commit**

```bash
git add src/agentruntime/AgentToolLoop.cs src/core/TectikaAgents.Core/Models/AgentRunContracts.cs tests/TectikaAgents.Tests/AgentToolLoopTests.cs
git commit -m "feat(runtime): HTTP-free agentic tool-loop with explore + control tools"
```

---

### Task 8: Wire `AgentToolLoop` into `FoundryAgentRuntime` + smart-budgeted context + live smoke

This task connects the real Foundry HTTP `SendRound` to the loop, makes `ContextManager` budget-aware, and verifies end-to-end against `proj-agentteam`.

**Files:** Modify `FoundryAgentRuntime.cs` (`RunTurnAsync` + DTOs for array input); Modify `ContextManager.cs`; add `RunTurnAsync` an `IProjectExplorer` (extend `IAgentRuntime`); Create `tests/live/foundry_loop_smoke.py`; Test `tests/TectikaAgents.Tests/ContextManagerBudgetTests.cs`.

- [ ] **Step 1 (context budget test):** add `ContextManagerBudgetTests` asserting: per-task `Prompt` appears in the assembled text; when total upstream content exceeds the budget, full content is replaced by each upstream's `Summary` + a "use get_artifact" note; under budget, full content is included. (Mirror the existing `ContextManagerTests` style; pass a small budget int into the new `Assemble` overload.)

- [ ] **Step 2:** Extend `ContextManager.Assemble` to accept the per-task prompt + a token budget (`FoundrySettings.MaxInputTokens`), trim `TaskBrief` to the most-recent N lines, drop the embedded INTERACTION_REQUIRED/Brief-Update/Decisions prose (now tools), and add the one-line exploration nudge. Implement budget by estimating tokens as `chars/4`; include full upstream until the budget, then summary + `(full content available via get_artifact taskId=…)`.

- [ ] **Step 3:** Extend `IAgentRuntime.RunTurnAsync` with an `IProjectExplorer explorer` parameter; update `MockAgentRuntime` (ignore it, keep returning a scripted outcome) and `FoundryAgentRuntime`. In `FoundryAgentRuntime.RunTurnAsync`, build an `AgentToolLoop(explorer)` and a `SendRound` that POSTs to `/openai/v1/responses` with `agent_reference` + `conversation`, mapping `function_call` output items → `ToolCall`, and `function_call_output` inputs on subsequent rounds (input becomes an array). Map `LoopResult` → `AgentRunOutcome` (Content=FinalText, RoundIntent, BriefUpdate, Control, Usage; `MaxRoundsHit` → `BudgetExceeded`). Add request DTOs for array input items (`{type:"function_call_output", call_id, output}`).

> The InvokeAgentActivity that *supplies* the explorer + interprets `Control` is **Stage 3**. For Stage 2, a temporary caller/unit context is fine; do not modify the orchestrator here.

- [ ] **Step 4 (build + unit sweep):** `dotnet build TectikaAgents.slnx` → 0 errors; `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → all pass.

- [ ] **Step 5 (LIVE smoke):** Create `tests/live/foundry_loop_smoke.py` (adapted from the verified probe): create a throwaway agent **with `TectikaToolSchema` tools**, open a conversation, send a prompt that triggers `get_board_overview`, feed a canned `function_call_output`, assert a final message returns, delete the agent. Run it:

```bash
az account show --query name -o tsv   # must be "Visual Studio Enterprise Subscription – MPN"
python3 tests/live/foundry_loop_smoke.py
```
Expected: prints `LOOP OK` after a function_call round and a final message; agent deleted (HTTP 200).

- [ ] **Step 6: Commit**

```bash
git add src/agentruntime/FoundryAgentRuntime.cs src/workflows/Services/ContextManager.cs \
        src/core/TectikaAgents.Core/Interfaces/IAgentRuntime.cs src/agentruntime/MockAgentRuntime.cs \
        tests/TectikaAgents.Tests/ContextManagerBudgetTests.cs tests/live/foundry_loop_smoke.py
git commit -m "feat: drive Foundry runs through the tool-loop + smart-budgeted context (live-smoked)"
```

---

### Task 9: Full build + test sweep

- [ ] **Step 1:** `dotnet build TectikaAgents.slnx` → `Build succeeded`, 0 errors.
- [ ] **Step 2:** `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → all pass (Stage 1 + Stage 2 classes).
- [ ] **Step 3:** `git status -s` → clean (ignore pre-existing `publish/`, `workflows-deploy.zip`).

---

## Self-Review

**1. Spec coverage (Stage 2 scope):**
- Tools on agent definition (verified contract) → Tasks 1, 3. ✓
- Hash versioned by toolset → Tasks 2, 4. ✓
- `IProjectExplorer` board-scoped explore tools → Tasks 5, 6. ✓
- Multi-round tool-loop + control tools replace prose markers → Task 7 (loop), Task 8 Step 2 (context drops prose). ✓
- Smart-budgeted context floor + per-task prompt → Task 8 Steps 1-2. ✓
- `MockAgentRuntime` stays usable offline → Tasks 4, 8 Step 3. ✓
- Live smoke (user-requested) → Task 8 Step 5. ✓
- Deferred to Stage 3 (correctly absent): `InvokeAgentActivity` supplying the explorer + interpreting `PendingControl`, `RunEvent` persistence, orchestration, chat API.

**2. Placeholder scan:** Tasks 1-7 contain complete code. Task 8 is described at component granularity (not full code) because it depends on signature choices finalized in Tasks 5-7 and on the live API; its sub-steps are concrete and testable. Acceptable as the integration capstone, but the executor should write the array-input DTOs by following the verified contract block at the top. No "TBD"/"handle edge cases". 

**3. Type consistency:** `AgentInstructionsHash.Compute(prompt, model, toolsVersion)` is used identically in Tasks 2/3/4. `TectikaToolSchema.Version` + `ToFoundryToolsJson()` used in Tasks 1/3. `IProjectExplorer` method names + DTOs match across Tasks 5/6/7. `AgentRunOutcome` new fields (`RoundIntent`, `Control`) defined in Task 7 Step 1 and consumed in Task 8 Step 3. `PendingControlKind` values (`HumanInput`/`Approval`/`Revision`) consistent in loop + tests. ✓
