# Artifact Handoff Model — Phase B (Agent Write-Path) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make agents *produce* the handoff shape — declare deliverables deliberately via new `declare_output` / `update_output` / `remove_output` tools (accumulated per-run, editable mid-session), and write a Final `Artifact` carrying `summary` (the agent's final message) + `outputs[]`, while keeping `Content` populated so no existing consumer breaks.

**Architecture:** Three new "control-style" tools register declared outputs as `OutputOp`s in `RoundExecutor`; the ops thread through `RoundProcessResult` → `RoundOutcome` to `RunAgentRoundActivity`, which accumulates them on `AgentTask.PendingOutputs` (reset per run, patched each round, mirroring the existing `TaskBrief` pattern) via a pure `OutputAccumulator.Apply`. At Final, the artifact is written with `Summary = Content = outcome.FinalText` and `Outputs = validated PendingOutputs`. A framework directive tells agents to declare deliverables and make their final message a concise handoff summary.

**Tech Stack:** C# / .NET 10, xUnit (`tests/TectikaAgents.Tests`), Cosmos (System.Text.Json serializer, JSON Patch), Azure Durable Functions.

**Builds on:** Plan A (merged) — `Output`, `OutputKind`, `InlineContent`, `Artifact.Outputs`, `EnsureHandoffShape`, and the frontend renderer already exist. This plan only adds *production*; the frontend already renders what it produces.

**Scope note:** Completes Spec 1 (`docs/superpowers/specs/2026-06-17-artifact-handoff-model-design.md`). Spec 2 (Code/git external outputs, the repo viewer) and the deferred items in the spec's §12 "Carried into Plan B" — concrete `ExternalRef.Locator` typing and external-output rendering — remain for the git work; this plan wires the **Document** kind end-to-end and adds the editable declare mechanism the spec requires.

---

## File Structure

**Create:**
- `src/core/TectikaAgents.Core/Models/OutputOp.cs` — `OutputOpKind` enum + `OutputOp` record (a declare/update/remove instruction).
- `src/core/TectikaAgents.Core/Models/OutputAccumulator.cs` — pure `Apply(current, ops)` reducer.
- `tests/TectikaAgents.Tests/OutputAccumulatorTests.cs`
- `tests/TectikaAgents.Tests/DeclareOutputToolTests.cs` (RoundExecutor behavior for the 3 tools)

**Modify:**
- `src/core/TectikaAgents.Core/Models/RoundContracts.cs` — add `OutputOps` to `RoundOutcome`.
- `src/core/TectikaAgents.Core/Models/AgentTask.cs` — add `PendingOutputs`.
- `src/agentruntime/TectikaToolSchema.cs` — 3 tool defs + `Version` bump.
- `src/agentruntime/RoundExecutor.cs` — handle 3 tools → `OutputOps`; add field to `RoundProcessResult`; helpers.
- `src/agentruntime/FoundryAgentRuntime.cs` — thread `p.OutputOps` into the 3 `RoundOutcome` constructions.
- `src/workflows/Services/WorkflowCosmosService.cs` — `PatchTaskPendingOutputsAsync`.
- `src/workflows/Services/ContextManager.cs` — framework directive guidance.
- `src/workflows/Activities/RunAgentRoundActivity.cs` — reset/accumulate/persist + artifact build.
- `tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs` — expect 3 new tool names.

---

## Task 1: `OutputOp` + `OutputAccumulator` (pure core logic)

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/OutputOp.cs`, `src/core/TectikaAgents.Core/Models/OutputAccumulator.cs`
- Test: `tests/TectikaAgents.Tests/OutputAccumulatorTests.cs`

- [ ] **Step 1: Write the failing test** — create `tests/TectikaAgents.Tests/OutputAccumulatorTests.cs`:

```csharp
using TectikaAgents.Core.Models;
using Xunit;

public class OutputAccumulatorTests
{
    private static Output Doc(string id, string content, string? label = null) => new()
    {
        Id = id, Kind = OutputKind.Document, Label = label,
        Inline = new InlineContent { ContentType = ArtifactContentType.Markdown, Content = content },
    };

    [Fact]
    public void Declare_AppendsOutput()
    {
        var result = OutputAccumulator.Apply([], [new OutputOp(OutputOpKind.Declare, "a", Declared: Doc("a", "hi"))]);
        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
        Assert.Equal("hi", result[0].Inline!.Content);
    }

    [Fact]
    public void Update_ChangesLabelAndInlineOfMatchingId()
    {
        var current = new List<Output> { Doc("a", "old", "Old") };
        var result = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Update, "a",
            Label: "New", Inline: new InlineContent { ContentType = ArtifactContentType.Json, Content = "new" })]);
        Assert.Single(result);
        Assert.Equal("New", result[0].Label);
        Assert.Equal("new", result[0].Inline!.Content);
        Assert.Equal(ArtifactContentType.Json, result[0].Inline!.ContentType);
    }

    [Fact]
    public void Update_LeavesOmittedFieldsUnchanged()
    {
        var current = new List<Output> { Doc("a", "keep", "Keep") };
        var result = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Update, "a", Label: "Renamed")]);
        Assert.Equal("Renamed", result[0].Label);
        Assert.Equal("keep", result[0].Inline!.Content); // inline untouched
    }

    [Fact]
    public void Update_UnknownId_IsNoOp()
    {
        var current = new List<Output> { Doc("a", "hi") };
        var result = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Update, "zzz", Label: "x")]);
        Assert.Single(result);
        Assert.Null(result[0].Label);
    }

    [Fact]
    public void Remove_DropsMatchingId_UnknownIsNoOp()
    {
        var current = new List<Output> { Doc("a", "1"), Doc("b", "2") };
        var afterRemove = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Remove, "a")]);
        Assert.Single(afterRemove);
        Assert.Equal("b", afterRemove[0].Id);

        var afterUnknown = OutputAccumulator.Apply(afterRemove, [new OutputOp(OutputOpKind.Remove, "nope")]);
        Assert.Single(afterUnknown);
    }

    [Fact]
    public void Apply_DoesNotMutateInputList()
    {
        var current = new List<Output> { Doc("a", "1") };
        _ = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Declare, "b", Declared: Doc("b", "2"))]);
        Assert.Single(current); // original unchanged
    }

    [Fact]
    public void Sequence_DeclareDeclareUpdateRemove()
    {
        var ops = new List<OutputOp>
        {
            new(OutputOpKind.Declare, "a", Declared: Doc("a", "A")),
            new(OutputOpKind.Declare, "b", Declared: Doc("b", "B")),
            new(OutputOpKind.Update, "a", Label: "Alpha"),
            new(OutputOpKind.Remove, "b"),
        };
        var result = OutputAccumulator.Apply([], ops);
        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
        Assert.Equal("Alpha", result[0].Label);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~OutputAccumulatorTests` → build error (types missing).

- [ ] **Step 3: Create `src/core/TectikaAgents.Core/Models/OutputOp.cs`:**

```csharp
namespace TectikaAgents.Core.Models;

/// <summary>An edit to a task's declared output set, produced by the
/// declare_output / update_output / remove_output tools.</summary>
public enum OutputOpKind { Declare, Update, Remove }

/// <summary>One declare/update/remove instruction. <see cref="Id"/> is the new
/// output's id (Declare) or the target output's id (Update/Remove). For Update,
/// a null <see cref="Label"/> or <see cref="Inline"/> means "leave unchanged".</summary>
public sealed record OutputOp(
    OutputOpKind Kind,
    string Id,
    Output? Declared = null,
    string? Label = null,
    InlineContent? Inline = null);
```

- [ ] **Step 4: Create `src/core/TectikaAgents.Core/Models/OutputAccumulator.cs`:**

```csharp
namespace TectikaAgents.Core.Models;

/// <summary>Pure reducer that applies a round's <see cref="OutputOp"/>s to the
/// task's accumulated declared-output set. Never mutates the input list.</summary>
public static class OutputAccumulator
{
    public static List<Output> Apply(IReadOnlyList<Output> current, IReadOnlyList<OutputOp> ops)
    {
        var list = current.ToList();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OutputOpKind.Declare:
                    if (op.Declared is not null) list.Add(op.Declared);
                    break;
                case OutputOpKind.Update:
                    var existing = list.FirstOrDefault(o => o.Id == op.Id);
                    if (existing is not null)
                    {
                        if (op.Label is not null) existing.Label = op.Label;
                        if (op.Inline is not null) existing.Inline = op.Inline;
                    }
                    break;
                case OutputOpKind.Remove:
                    list.RemoveAll(o => o.Id == op.Id);
                    break;
            }
        }
        return list;
    }
}
```

- [ ] **Step 5: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~OutputAccumulatorTests` → 7 pass.

- [ ] **Step 6: Commit:**
```bash
git add src/core/TectikaAgents.Core/Models/OutputOp.cs src/core/TectikaAgents.Core/Models/OutputAccumulator.cs tests/TectikaAgents.Tests/OutputAccumulatorTests.cs
git commit -m "feat(core): add OutputOp + OutputAccumulator for per-run declared outputs"
```

---

## Task 2: Tool schema — declare/update/remove_output defs + version bump

**Files:**
- Modify: `src/agentruntime/TectikaToolSchema.cs`
- Test: `tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs`

- [ ] **Step 1: Update the catalog test first.** In `tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs`, in the test `Catalog_ContainsExploreAndControlTools`, add the three names to the asserted set. The expected-names array currently is:

```csharp
        foreach (var n in new[] { "get_board_overview", "search_tasks", "get_task", "get_artifact",
                                  "request_human_input", "request_approval", "request_revision",
                                  "update_brief", "round_intent" })
            Assert.Contains(n, names);
```

Replace it with:

```csharp
        foreach (var n in new[] { "get_board_overview", "search_tasks", "get_task", "get_artifact",
                                  "request_human_input", "request_approval", "request_revision",
                                  "update_brief", "round_intent",
                                  "declare_output", "update_output", "remove_output" })
            Assert.Contains(n, names);
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~TectikaToolSchemaTests` → fails (3 names not in catalog).

- [ ] **Step 3: Add the three tool definitions.** In `src/agentruntime/TectikaToolSchema.cs`, inside the `Definitions` array, immediately AFTER the `update_brief` entry (the line starting `new("update_brief", ...`), add:

```csharp
        new("declare_output", "Register a finished DELIVERABLE for this task — a document/section the user and downstream tasks will receive. Call this once per real product of your work. Do NOT call it for exploration, debugging, or fix-up steps. Returns the output's id; pass that id to update_output or remove_output to revise it later this session.",
            new Dictionary<string, ToolProp> {
                ["content"] = new("string", "The deliverable's full content."),
                ["label"] = new("string", "Short label, e.g. 'Itinerary' or 'API spec'."),
                ["contentType"] = new("string", "Content format (default Markdown).", new[] { "Markdown", "Json", "Data", "Code" }),
            }, ["content"]),
        new("update_output", "Revise a deliverable you previously declared (by its id) — replace its content, label, or format. Use this when your work evolved during the session.",
            new Dictionary<string, ToolProp> {
                ["id"] = new("string", "The id returned by declare_output."),
                ["content"] = new("string", "Replacement content (omit to keep current)."),
                ["label"] = new("string", "Replacement label (omit to keep current)."),
                ["contentType"] = new("string", "Replacement format (omit to keep current).", new[] { "Markdown", "Json", "Data", "Code" }),
            }, ["id"]),
        new("remove_output", "Remove a deliverable you previously declared (by its id) that is no longer part of the result.",
            new Dictionary<string, ToolProp> {
                ["id"] = new("string", "The id returned by declare_output."),
            }, ["id"]),
```

- [ ] **Step 4: Bump the schema version** so agents re-sync their tool set. In the same file, change the `Version` constant:

```csharp
    public const string Version = "tools-v4";
```
to:
```csharp
    public const string Version = "tools-v5";
```

- [ ] **Step 5: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~TectikaToolSchemaTests` → passes.

- [ ] **Step 6: Commit:**
```bash
git add src/agentruntime/TectikaToolSchema.cs tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs
git commit -m "feat(agentruntime): add declare/update/remove_output tool defs; bump schema to tools-v5"
```

---

## Task 3: RoundExecutor handles the 3 tools → emits `OutputOp`s

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/RoundContracts.cs` (add field to `RoundOutcome`)
- Modify: `src/agentruntime/RoundExecutor.cs` (field on `RoundProcessResult`, 3 cases, helpers)
- Modify: `src/agentruntime/FoundryAgentRuntime.cs` (thread `OutputOps` into outcomes)
- Test: `tests/TectikaAgents.Tests/DeclareOutputToolTests.cs`

- [ ] **Step 1: Write the failing test** — create `tests/TectikaAgents.Tests/DeclareOutputToolTests.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class DeclareOutputToolTests
{
    private static ToolCall FC(string name, object args) =>
        new(name, JsonSerializer.Serialize(args), $"call_{name}");

    private static Task<RoundProcessResult> Run(params ToolCall[] calls) =>
        RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(calls), new NullProjectExplorer(), (_, __) => { },
            null, null, null, null, null, null, default);

    [Fact]
    public async Task DeclareOutput_EmitsDeclareOp_AndReturnsId()
    {
        var r = await Run(FC("declare_output", new { content = "the plan", label = "Itinerary", contentType = "Markdown" }));

        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Declare, op.Kind);
        Assert.NotNull(op.Declared);
        Assert.Equal(OutputKind.Document, op.Declared!.Kind);
        Assert.Equal("Itinerary", op.Declared.Label);
        Assert.Equal("the plan", op.Declared.Inline!.Content);
        Assert.Equal(op.Id, op.Declared.Id); // op id matches the declared output's id

        // the model is told the new id
        Assert.Contains(op.Id, r.ToolOutputs.Single().Output);
    }

    [Fact]
    public async Task DeclareOutput_DefaultsContentTypeToMarkdown()
    {
        var r = await Run(FC("declare_output", new { content = "x" }));
        Assert.Equal(ArtifactContentType.Markdown, Assert.Single(r.OutputOps).Declared!.Inline!.ContentType);
    }

    [Fact]
    public async Task UpdateOutput_EmitsUpdateOp_WithProvidedFieldsOnly()
    {
        var r = await Run(FC("update_output", new { id = "abc", label = "Renamed" }));
        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Update, op.Kind);
        Assert.Equal("abc", op.Id);
        Assert.Equal("Renamed", op.Label);
        Assert.Null(op.Inline); // content not provided
        Assert.Equal("ok", r.ToolOutputs.Single().Output);
    }

    [Fact]
    public async Task RemoveOutput_EmitsRemoveOp()
    {
        var r = await Run(FC("remove_output", new { id = "abc" }));
        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Remove, op.Kind);
        Assert.Equal("abc", op.Id);
        Assert.Equal("ok", r.ToolOutputs.Single().Output);
    }

    [Fact]
    public async Task NonOutputRound_HasEmptyOutputOps()
    {
        var r = await Run(FC("round_intent", new { text = "go" }));
        Assert.Empty(r.OutputOps);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~DeclareOutputToolTests` → build error (`RoundProcessResult.OutputOps` missing).

- [ ] **Step 3: Add `OutputOps` to `RoundOutcome`.** In `src/core/TectikaAgents.Core/Models/RoundContracts.cs`, change the `RoundOutcome` record's final parameter from:

```csharp
    string CompletionId,
    string? Error = null);
```
to:
```csharp
    string CompletionId,
    string? Error = null,
    IReadOnlyList<OutputOp>? OutputOps = null);
```

- [ ] **Step 4: Add `OutputOps` to `RoundProcessResult` and handle the 3 tools.** In `src/agentruntime/RoundExecutor.cs`:

(a) Add a field to the `RoundProcessResult` record — change:
```csharp
    IReadOnlyList<RoundToolCall> ToolCalls);      // summarised trace for RunEvents
```
to:
```csharp
    IReadOnlyList<RoundToolCall> ToolCalls,        // summarised trace for RunEvents
    IReadOnlyList<OutputOp> OutputOps);            // declared-output edits this round
```

(b) At the top of `ExecuteOneRoundAsync`, where the other accumulators are declared (next to `var outputs = new List<ToolOutput>();`), add:
```csharp
        var ops = new List<OutputOp>();
```

(c) The early return for the no-tool-calls case — change:
```csharp
        if (resp.ToolCalls is null || resp.ToolCalls.Count == 0)
            return new RoundProcessResult(true, resp.FinalText ?? "", [], null, null, null, null, []);
```
to:
```csharp
        if (resp.ToolCalls is null || resp.ToolCalls.Count == 0)
            return new RoundProcessResult(true, resp.FinalText ?? "", [], null, null, null, null, [], []);
```

(d) Add three `case`s in the `switch (call.Name)`, immediately after the `update_brief` case:
```csharp
            case "declare_output":
            {
                var newId = Guid.NewGuid().ToString();
                var declared = new Output
                {
                    Id = newId,
                    Kind = OutputKind.Document,
                    Label = StrOrNull(args, "label"),
                    Inline = new InlineContent { ContentType = ParseContentType(StrOrNull(args, "contentType")), Content = Str(args, "content") },
                };
                ops.Add(new OutputOp(OutputOpKind.Declare, newId, Declared: declared));
                outputs.Add(new(call.CallId, $"{{\"id\":\"{newId}\"}}"));
                traced.Add(new("declare_output", declared.Label ?? "Document", $"declared {newId}"));
                break;
            }
            case "update_output":
            {
                var id = Str(args, "id");
                InlineContent? inline = null;
                var newContent = StrOrNull(args, "content");
                if (newContent is not null)
                    inline = new InlineContent { ContentType = ParseContentType(StrOrNull(args, "contentType")), Content = newContent };
                ops.Add(new OutputOp(OutputOpKind.Update, id, Label: StrOrNull(args, "label"), Inline: inline));
                outputs.Add(new(call.CallId, "ok"));
                traced.Add(new("update_output", id, "ok"));
                break;
            }
            case "remove_output":
            {
                var id = Str(args, "id");
                ops.Add(new OutputOp(OutputOpKind.Remove, id));
                outputs.Add(new(call.CallId, "ok"));
                traced.Add(new("remove_output", id, "ok"));
                break;
            }
```

(e) The final return — change:
```csharp
        return new RoundProcessResult(false, null, outputs, openControlCallId, control, intent, brief, traced);
```
to:
```csharp
        return new RoundProcessResult(false, null, outputs, openControlCallId, control, intent, brief, traced, ops);
```

(f) Add two private helpers next to the existing `Str`/`StrArr`/`IntOrNull` helpers in `RoundExecutor`:
```csharp
    private static string? StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static ArtifactContentType ParseContentType(string? s) =>
        Enum.TryParse<ArtifactContentType>(s, ignoreCase: true, out var v) ? v : ArtifactContentType.Markdown;
```
(If `RoundExecutor.cs` does not already have `using TectikaAgents.Core.Models;`, add it — it references `RoundToolCall`/`PendingControl` so it almost certainly does.)

- [ ] **Step 5: Thread `OutputOps` into the outcomes.** In `src/agentruntime/FoundryAgentRuntime.cs`, in `RunRoundAsync`, add `OutputOps: p.OutputOps` as the last argument to each of the three `new RoundOutcome(...)` constructions (Final, AwaitUser, Continue). For example the Continue one changes from:
```csharp
        return new RoundOutcome(RoundKind.Continue, null, next, null, null, p.RoundIntent, p.BriefUpdate, p.ToolCalls, usage, id);
```
to:
```csharp
        return new RoundOutcome(RoundKind.Continue, null, next, null, null, p.RoundIntent, p.BriefUpdate, p.ToolCalls, usage, id, OutputOps: p.OutputOps);
```
Apply the same `, OutputOps: p.OutputOps` to the `RoundKind.Final` and `RoundKind.AwaitUser` returns. (MockAgentRuntime needs no change — `OutputOps` defaults to null.)

- [ ] **Step 6: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~DeclareOutputToolTests` → 5 pass. Then build the runtime: `dotnet build src/agentruntime` → 0 errors.

- [ ] **Step 7: Commit:**
```bash
git add src/core/TectikaAgents.Core/Models/RoundContracts.cs src/agentruntime/RoundExecutor.cs src/agentruntime/FoundryAgentRuntime.cs tests/TectikaAgents.Tests/DeclareOutputToolTests.cs
git commit -m "feat(agentruntime): RoundExecutor emits OutputOps for declare/update/remove_output"
```

---

## Task 4: `AgentTask.PendingOutputs` + Cosmos patch

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/AgentTask.cs`
- Modify: `src/workflows/Services/WorkflowCosmosService.cs`

No new unit test (mechanical model field + Cosmos patch, verified by build + Task 5's suite run).

- [ ] **Step 1: Add the field.** In `src/core/TectikaAgents.Core/Models/AgentTask.cs`, immediately after the `taskBrief` property, add:

```csharp
    [JsonPropertyName("pendingOutputs")]
    public List<Output> PendingOutputs { get; set; } = [];
```

- [ ] **Step 2: Add the patch method.** In `src/workflows/Services/WorkflowCosmosService.cs`, immediately after `PatchTaskBriefAsync`, add:

```csharp
    public async Task PatchTaskPendingOutputsAsync(string boardId, string taskId, List<Output> outputs, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/pendingOutputs", outputs) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }
```

- [ ] **Step 3: Build** — `dotnet build src/workflows` → 0 errors.

- [ ] **Step 4: Commit:**
```bash
git add src/core/TectikaAgents.Core/Models/AgentTask.cs src/workflows/Services/WorkflowCosmosService.cs
git commit -m "feat: add AgentTask.PendingOutputs + PatchTaskPendingOutputsAsync"
```

---

## Task 5: RunAgentRoundActivity — accumulate per-run + write handoff artifact

**Files:**
- Modify: `src/workflows/Activities/RunAgentRoundActivity.cs`

Integration code in a Durable activity (Cosmos-bound); verified by build + full suite + the manual check below. The reducer it calls (`OutputAccumulator.Apply`) is unit-tested in Task 1.

- [ ] **Step 1: Accumulate declared outputs each round.** In `src/workflows/Activities/RunAgentRoundActivity.cs`, find the brief-update block:

```csharp
        if (!string.IsNullOrEmpty(outcome.BriefUpdate))
        {
            task.TaskBrief += $"\n[{role.DisplayName}, {Short(input.RunId)}, Round {input.Round}]: {outcome.BriefUpdate}";
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
        }
```

Immediately AFTER that block, add:

```csharp
        // Per-run declared outputs: reset at the start of a run, fold in this round's declare/update/remove ops.
        if (input.Round == 0)
            task.PendingOutputs = [];
        if (outcome.OutputOps is { Count: > 0 })
            task.PendingOutputs = OutputAccumulator.Apply(task.PendingOutputs, outcome.OutputOps);
        if (input.Round == 0 || outcome.OutputOps is { Count: > 0 })
            await _cosmos.PatchTaskPendingOutputsAsync(input.BoardId, input.TaskId, task.PendingOutputs, ct);
```

- [ ] **Step 2: Write the handoff artifact at Final.** In the same file, find the artifact-creation block and change it from:

```csharp
            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = outcome.FinalText ?? "",
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", $"Completion: {outcome.CompletionId}"],
            };
```
to:
```csharp
            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = outcome.FinalText ?? "",        // back-compat: Content == summary; readers + EnsureHandoffShape still work
                Summary = outcome.FinalText ?? "",         // the agent's final message is the handoff summary
                Outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList(),  // deliberately declared deliverables
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", $"Completion: {outcome.CompletionId}"],
            };
```

(`OutputAccumulator`, `Output`, and `IsValid()` are all in `TectikaAgents.Core.Models`, already imported by this file since it uses `Artifact`. `System.Linq` is available via ImplicitUsings.)

- [ ] **Step 3: Build + full suite** — `dotnet build src/workflows` then `dotnet test tests/TectikaAgents.Tests` → build clean, all tests pass (no regressions).

- [ ] **Step 4: Manual check (mock agents).** Per the project's AgentBoard QA flow with mock agents: the mock runtime declares no outputs, so a completed task still produces an artifact whose `Summary`/`Content` are the final text and whose `Outputs` is empty → the right pane renders via `EnsureHandoffShape` exactly as before (no regression). Confirm a task still completes and its artifact renders. (End-to-end real declared outputs are exercised against Foundry agents once the schema re-syncs to tools-v5.)

- [ ] **Step 5: Commit:**
```bash
git add src/workflows/Activities/RunAgentRoundActivity.cs
git commit -m "feat(workflows): accumulate declared outputs per run; write summary+outputs artifact at Final"
```

---

## Task 6: Framework directive — tell agents to declare outputs + summarize

**Files:**
- Modify: `src/workflows/Services/ContextManager.cs`
- Test: `tests/TectikaAgents.Tests/ContextManagerTests.cs`

- [ ] **Step 1: Write the failing test.** In `tests/TectikaAgents.Tests/ContextManagerTests.cs`, mirror the existing test setup in that file (same `ContextManager` construction and `BuildUserContentAsync`/`Assemble` call the other tests use) and add a `[Fact]`:

```csharp
    [Fact]
    public async Task Context_InstructsAgentToDeclareOutputsAndSummarize()
    {
        // Arrange exactly as the other tests in this file build a ContextManager + inputs,
        // then assemble the round-0 context string `context`.
        // (Reuse this file's existing helpers/fixtures for role/task/board/upstream.)

        Assert.Contains("declare_output", context);
        Assert.Contains("handoff summary", context);
    }
```
(Read `tests/TectikaAgents.Tests/ContextManagerTests.cs` for the exact fixture/helpers and produce a compiling test that obtains the assembled `context` string the same way the existing tests do.)

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~ContextManagerTests` → the new test fails (strings absent).

- [ ] **Step 3: Update the directive.** In `src/workflows/Services/ContextManager.cs`, find:

```csharp
        sb.AppendLine("\nComplete the task. Be thorough and production-ready. Your final message is the deliverable.");
```
and replace it with:
```csharp
        sb.AppendLine("\nComplete the task. Be thorough and production-ready.");
        sb.AppendLine("Register each finished deliverable with the declare_output tool (revise it later with update_output / remove_output by its id). Exploration, debugging and fix-up steps are NOT deliverables — do not declare those.");
        sb.AppendLine("Your final message is a concise handoff summary: what you accomplished and where the deliverables are — NOT the deliverables themselves.");
```

- [ ] **Step 4: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~ContextManagerTests` → passes. Then run the full suite: `dotnet test tests/TectikaAgents.Tests` → all pass.

- [ ] **Step 5: Commit:**
```bash
git add src/workflows/Services/ContextManager.cs tests/TectikaAgents.Tests/ContextManagerTests.cs
git commit -m "feat(workflows): direct agents to declare deliverables and write a handoff summary"
```

---

## Self-Review

**Spec coverage (against `2026-06-17-artifact-handoff-model-design.md`):**
- §4 two-step finalization (deliberate declaration, isolated from work tools) → Tasks 2, 3 (declare/update/remove tools) + Task 6 (directive). Summary = final message. ✓
- §4 outputs editable within a session (update/remove by id, auditable) → `OutputOp` + `OutputAccumulator` (Task 1), tools (Task 3), trace entries in `RoundExecutor`. ✓
- §6 downstream consumes summary + refs, not raw bodies → achieved implicitly: new artifacts set `Content = Summary` (the lean handoff), so `ContextManager` now feeds the summary; full deliverables live in `Outputs` and are not pushed downstream. ✓ (External refs land with Spec 2.)
- §10 Document kind wired end-to-end; production added without external providers → Tasks 2–5. ✓
- §9 validation (`IsValid`) enforced on the write path → Task 5 filters `Outputs` by `IsValid()`. ✓

**Placeholder scan:** All code steps show full code. The only "read the existing file" instruction is Task 6 Step 1 (mirror the existing `ContextManagerTests` fixture) — necessary because that test file's fixtures are project-specific; the assertion and directive strings are given verbatim. No TODO/TBD/"handle edge cases". 

**Type consistency:** `OutputOp`/`OutputOpKind` defined in Task 1 and used identically in Tasks 3 & 5; `OutputAccumulator.Apply(IReadOnlyList<Output>, IReadOnlyList<OutputOp>)` signature matches its call in Task 5; `RoundProcessResult.OutputOps` (Task 3d) and `RoundOutcome.OutputOps` (Task 3 step 3) names match their reads in `FoundryAgentRuntime` (Task 3 step 5) and `RunAgentRoundActivity` (Task 5); `PendingOutputs` (Task 4) matches its use in Task 5; the 3 tool names match across Task 2 (defs), Task 2 (catalog test), and Task 3 (executor cases).

**Risk notes (carried, not blockers):** `ExternalRef.Locator` typing and external-output rendering remain for Spec 2 (this plan only produces Document inline outputs). The RunAgentRoundActivity wiring (Task 5) is integration code verified by build + suite + manual mock check rather than a unit test, because it is Durable/Cosmos-bound; its core reducer is unit-tested in Task 1.
