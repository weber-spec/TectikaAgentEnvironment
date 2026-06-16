# Steerable Human-in-the-Loop (Unified Approvals) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a steerable (chat) agent run pauses on a control tool, persist a `HumanInteraction` record so the request appears in the chat, the Approvals tab, and notifications, and can be answered from any of them to resume the run. Plus: Enter sends a chat message, Shift+Enter inserts a newline.

**Architecture:** A paused steerable round (`RoundKind.AwaitUser`) creates a `HumanInteraction` tagged `Origin=Steerable` (reusing the existing record/pending-list/InteractionCard/notification pipeline). The interaction-respond endpoint branches on `Origin`: steerable interactions resume by raising the `user_message` external event (rendered to natural-language text) instead of the old-pipeline `interaction-{step}` event. Free-typed chat replies also resolve the pending interaction so every surface stays consistent.

**Tech Stack:** .NET (C#, Durable Functions, Cosmos), xUnit (`tests/TectikaAgents.Tests`), Next.js 16 + React + Tailwind (`src/web/tectika-board`). No JS test runner — web verified via `tsc` + `eslint` + `next build --webpack`.

**Spec:** `docs/superpowers/specs/2026-06-16-steerable-human-in-the-loop-design.md`

---

## File Structure

- **Modify** `src/core/TectikaAgents.Core/Models/HumanInteraction.cs` — add `InteractionOrigin` enum + `Origin` field.
- **Create** `src/workflows/Services/SteerableInteractionFactory.cs` — pure builder: `PendingControl` → `HumanInteraction` (stable id). (Namespace `TectikaAgents.Workflows.Services`, matching `WorkflowCosmosService.cs` in the same folder.)
- **Modify** `src/workflows/.../Services/WorkflowCosmosService.cs` — `UpsertInteractionAsync` (idempotent by stable id).
- **Modify** `src/workflows/.../Activities/RunAgentRoundActivity.cs` — on `AwaitUser`, build + upsert the interaction + publish `interaction_required`.
- **Create** `src/api/TectikaAgents.Api/Services/SteerableInteractionReply.cs` — pure renderer: resolved `HumanInteraction` → reply text.
- **Modify** `src/api/.../Controllers/InteractionsController.cs` — `Origin==Steerable` branch + `RaiseUserMessageEventAsync`.
- **Modify** `src/api/.../Services/ChatService.cs` — resolve the pending steerable interaction when injecting into an awaiting run.
- **Modify** `src/web/tectika-board/src/lib/types.ts` — add `origin` to the `HumanInteraction` TS interface.
- **Modify** `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` — render `InteractionCard` in chat for the task's pending interaction; Enter-to-send.
- **Create** tests in `tests/TectikaAgents.Tests/` for the two pure helpers.

> The factory and the reply-renderer are deliberately extracted as pure static helpers so the control→type mapping and the text rendering are unit-testable; the activity/controller/chat wiring is verified by `dotnet build` + the live smoke (these touch Cosmos/Durable and are integration-level, consistent with how this codebase is tested).

---

## Task 1: Add `Origin` to `HumanInteraction` (Core)

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/HumanInteraction.cs`

- [ ] **Step 1: Add the enum and field**

In `src/core/TectikaAgents.Core/Models/HumanInteraction.cs`, add the `Origin` property to the `HumanInteraction` class immediately after the `IdentityToBeUsed` property (currently the last property, ending at line 74):

```csharp
    [JsonPropertyName("identityToBeUsed")]
    public string? IdentityToBeUsed { get; set; }

    /// <summary>Which orchestration created this request, so the responder resumes it correctly:
    /// Pipeline = old TaskPipelineOrchestrator (interaction-{step} event); Steerable = chat run
    /// (user_message event). Defaults to Pipeline so all existing records keep their behavior.</summary>
    [JsonPropertyName("origin")]
    public InteractionOrigin Origin { get; set; } = InteractionOrigin.Pipeline;
```

Then add the enum next to the existing enums at the bottom of the file (after `public enum InteractionStatus { Pending, Responded, Expired }` on line 132):

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionOrigin { Pipeline, Steerable }
```

- [ ] **Step 2: Build Core**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/HumanInteraction.cs
git commit -m "feat(core): HumanInteraction.Origin (Pipeline | Steerable)"
```

---

## Task 2: `SteerableInteractionFactory` + test (Workflows)

**Files:**
- Create: `src/workflows/Services/SteerableInteractionFactory.cs`
- Test: `tests/TectikaAgents.Tests/SteerableInteractionFactoryTests.cs`

> The new file lives alongside `src/workflows/Services/WorkflowCosmosService.cs` and uses the same namespace that file declares: `namespace TectikaAgents.Workflows.Services;`.

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/SteerableInteractionFactoryTests.cs`:

```csharp
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class SteerableInteractionFactoryTests
{
    [Fact]
    public void Approval_control_maps_to_Approval_type_with_stable_id()
    {
        var control = new PendingControl(PendingControlKind.Approval, "Deploy to prod?");
        var i = SteerableInteractionFactory.Build("run1", "task1", "board1", "tenant1", 2, "auditor@x.com", control);

        Assert.Equal("run1-r2-interaction", i.Id);
        Assert.Equal(InteractionType.Approval, i.Type);
        Assert.Equal(InteractionOrigin.Steerable, i.Origin);
        Assert.Equal(InteractionStatus.Pending, i.Status);
        Assert.Equal("Deploy to prod?", i.ActionDescription);
        Assert.Equal(new List<string> { "auditor@x.com" }, i.RequestedFrom);
        Assert.Equal(2, i.StepIndex);
        Assert.Null(i.QuestionOptions);
    }

    [Fact]
    public void HumanInput_with_options_maps_to_Question_with_options()
    {
        var control = new PendingControl(PendingControlKind.HumanInput, "Which DB?", new[] { "Postgres", "Cosmos" });
        var i = SteerableInteractionFactory.Build("run1", "task1", "board1", "tenant1", 0, null, control);

        Assert.Equal(InteractionType.Question, i.Type);
        Assert.Equal("Which DB?", i.Question);
        Assert.Equal(new List<string> { "Postgres", "Cosmos" }, i.QuestionOptions);
        Assert.Empty(i.RequestedFrom); // no auditor → empty
    }

    [Fact]
    public void Revision_maps_to_free_text_Question()
    {
        var control = new PendingControl(PendingControlKind.Revision, "Please clarify the scope.");
        var i = SteerableInteractionFactory.Build("run1", "task1", "board1", "tenant1", 1, null, control);

        Assert.Equal(InteractionType.Question, i.Type);
        Assert.Equal("Please clarify the scope.", i.Question);
        Assert.Null(i.QuestionOptions);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~SteerableInteractionFactory"
```
Expected: FAIL to compile — `SteerableInteractionFactory` does not exist.

- [ ] **Step 3: Create the factory**

Create the file (path/namespace per the note above):

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Builds the persisted <see cref="HumanInteraction"/> for a paused steerable run from the
/// round's <see cref="PendingControl"/>. Pure + deterministic (stable id) so the control→type mapping
/// is unit-testable and the upsert is idempotent across Durable activity retries.</summary>
public static class SteerableInteractionFactory
{
    public static HumanInteraction Build(
        string runId, string taskId, string boardId, string tenantId, int round,
        string? humanAuditorId, PendingControl control)
    {
        var interaction = new HumanInteraction
        {
            Id = $"{runId}-r{round}-interaction",
            TenantId = tenantId,
            RunId = runId,
            TaskId = taskId,
            BoardId = boardId,
            StepIndex = round,
            Origin = InteractionOrigin.Steerable,
            Status = InteractionStatus.Pending,
            ActionDescription = control.Text,
            RequestedFrom = string.IsNullOrEmpty(humanAuditorId) ? [] : [humanAuditorId],
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(48),
            Type = control.Kind == PendingControlKind.Approval ? InteractionType.Approval : InteractionType.Question,
        };
        if (interaction.Type == InteractionType.Question)
        {
            interaction.Question = control.Text;
            if (control.Options is { Count: > 0 })
                interaction.QuestionOptions = control.Options.ToList();
        }
        return interaction;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~SteerableInteractionFactory"
```
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/workflows tests/TectikaAgents.Tests/SteerableInteractionFactoryTests.cs
git commit -m "feat(workflows): SteerableInteractionFactory (control -> HumanInteraction)"
```

---

## Task 3: Persist the interaction on a steerable pause (Workflows)

**Files:**
- Modify: `src/workflows/.../Services/WorkflowCosmosService.cs`
- Modify: `src/workflows/.../Activities/RunAgentRoundActivity.cs`

- [ ] **Step 1: Add `UpsertInteractionAsync` to `WorkflowCosmosService`**

In `WorkflowCosmosService.cs`, find `CreateInteractionAsync` (around line 322):

```csharp
    public async Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await C("humanInteractions").CreateItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
```

Add this method immediately above it (idempotent upsert by stable id, partitioned by `RunId` to match):

```csharp
    /// <summary>Idempotent create-or-replace by stable id — used for steerable interactions so a
    /// Durable activity retry can't create a duplicate request.</summary>
    public async Task<HumanInteraction> UpsertInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await C("humanInteractions").UpsertItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }
```

- [ ] **Step 2: Wire the activity**

In `RunAgentRoundActivity.cs`, find the trace-persistence loop (lines 113-118):

```csharp
        // Persist the round trace (hierarchical) and mirror each event over SSE — live and stored share one shape.
        foreach (var ev in RunEventFactory.BuildRoundEvents(input.RunId, input.TaskId, input.Round, outcome, artifactId))
        {
            var saved = await _cosmos.CreateRunEventAsync(ev, ct);
            await _events.PublishRunEventAsync(saved, ct);
        }
```

Add this block immediately after that loop (before `return new RoundActivityResult(...)`):

```csharp
        // A steerable control tool paused the run — persist a HumanInteraction so the request surfaces
        // in the Approvals tab + notifications (and the chat), answerable from any of them.
        if (outcome.Kind == RoundKind.AwaitUser && outcome.Control is not null)
        {
            var interaction = SteerableInteractionFactory.Build(
                input.RunId, input.TaskId, input.BoardId, input.TenantId, input.Round,
                task.HumanAuditorId, outcome.Control);
            var savedInteraction = await _cosmos.UpsertInteractionAsync(interaction, ct);
            await _events.PublishInteractionRequiredAsync(
                input.RunId, input.TaskId, input.Round, savedInteraction.Id, savedInteraction.Type.ToString(), ct);
        }
```

`RunAgentRoundActivity` already has `using TectikaAgents.Core.Models;` (for `RoundKind`, `PendingControl`) and `using TectikaAgents.Workflows.Services;` (for `WorkflowCosmosService`); `SteerableInteractionFactory` is in that same namespace, so no new `using` is required. Verify `task` (the loaded `AgentTask`) and `outcome` are both in scope at this point (they are — `task` is loaded near line 50, `outcome` near line 77).

- [ ] **Step 3: Build the workflows project**

Run (use the workflows `.csproj` path you confirmed in Task 2):
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet build src/workflows/*/*.csproj 2>/dev/null || dotnet build $(find src/workflows -name '*.csproj' | head -1)
```
Expected: Build succeeded.

- [ ] **Step 4: Run the full test suite to confirm nothing regressed**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
```
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/workflows
git commit -m "feat(workflows): persist steerable agent request on AwaitUser"
```

---

## Task 4: `SteerableInteractionReply` renderer + test (API)

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/SteerableInteractionReply.cs`
- Test: `tests/TectikaAgents.Tests/SteerableInteractionReplyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/SteerableInteractionReplyTests.cs`:

```csharp
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class SteerableInteractionReplyTests
{
    [Fact]
    public void Approved_renders_Approved()
    {
        var i = new HumanInteraction { Type = InteractionType.Approval, Approved = true };
        Assert.Equal("Approved.", SteerableInteractionReply.Render(i));
    }

    [Fact]
    public void Approved_with_notes_appends_notes()
    {
        var i = new HumanInteraction { Type = InteractionType.Approval, Approved = true, Notes = "ship it" };
        Assert.Equal("Approved. ship it", SteerableInteractionReply.Render(i));
    }

    [Fact]
    public void Rejected_renders_Rejected()
    {
        var i = new HumanInteraction { Type = InteractionType.Approval, Approved = false };
        Assert.Equal("Rejected.", SteerableInteractionReply.Render(i));
    }

    [Fact]
    public void Question_renders_the_answer()
    {
        var i = new HumanInteraction { Type = InteractionType.Question, Answer = "Use Postgres" };
        Assert.Equal("Use Postgres", SteerableInteractionReply.Render(i));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~SteerableInteractionReply"
```
Expected: FAIL to compile — `SteerableInteractionReply` does not exist.

- [ ] **Step 3: Create the renderer**

Create `src/api/TectikaAgents.Api/Services/SteerableInteractionReply.cs`:

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>Renders a resolved steerable <see cref="HumanInteraction"/> into the natural-language text
/// fed back to the agent as the control tool's output. The steerable loop resumes on a
/// <c>user_message</c> string, so the structured decision is flattened to a clear sentence.</summary>
public static class SteerableInteractionReply
{
    public static string Render(HumanInteraction i) => i.Type switch
    {
        InteractionType.Approval => (i.Approved == true ? "Approved." : "Rejected.")
            + (string.IsNullOrWhiteSpace(i.Notes) ? "" : " " + i.Notes.Trim()),
        InteractionType.Selection => SelectedTitle(i) ?? i.Answer ?? "",
        _ => i.Answer ?? "",
    };

    private static string? SelectedTitle(HumanInteraction i) =>
        i.SelectedIndex is int idx && i.Items is { } items && idx >= 0 && idx < items.Count
            ? items[idx].Title
            : null;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~SteerableInteractionReply"
```
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/SteerableInteractionReply.cs tests/TectikaAgents.Tests/SteerableInteractionReplyTests.cs
git commit -m "feat(api): SteerableInteractionReply renderer"
```

---

## Task 5: Resume steerable runs from the respond endpoint (API)

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/InteractionsController.cs`

- [ ] **Step 1: Branch the resume on `Origin`**

In `InteractionsController.cs`, find the "Wake up orchestrator" block (lines 117-122):

```csharp
        // Wake up orchestrator
        var run = await _cosmos.GetRunAsync(interaction.TaskId, req.RunId, ct);
        if (run?.DurableFunctionInstanceId is not null)
            await RaiseInteractionEventAsync(run.DurableFunctionInstanceId, interaction.StepIndex, payload, ct);
        else
            _logger.LogWarning("[InteractionReply] no DurableFunctionInstanceId for run {RunId} — cannot wake orchestrator", req.RunId);
```

Replace it with:

```csharp
        // Wake up orchestrator. Steerable runs resume on a `user_message` string; the old pipeline
        // resumes on the structured `interaction-{step}` event.
        var run = await _cosmos.GetRunAsync(interaction.TaskId, req.RunId, ct);
        if (run?.DurableFunctionInstanceId is not null)
        {
            if (interaction.Origin == InteractionOrigin.Steerable)
                await RaiseUserMessageEventAsync(run.DurableFunctionInstanceId, SteerableInteractionReply.Render(interaction), ct);
            else
                await RaiseInteractionEventAsync(run.DurableFunctionInstanceId, interaction.StepIndex, payload, ct);
        }
        else
            _logger.LogWarning("[InteractionReply] no DurableFunctionInstanceId for run {RunId} — cannot wake orchestrator", req.RunId);
```

- [ ] **Step 2: Add `RaiseUserMessageEventAsync`**

In the same file, add this method immediately after the existing `RaiseInteractionEventAsync` method (it ends at line 163, just before the final closing brace of the class):

```csharp
    private async Task RaiseUserMessageEventAsync(string instanceId, string text, CancellationToken ct)
    {
        var baseUrl = _durableSettings.StartUrl;
        var managementBase = baseUrl[..baseUrl.IndexOf("/api/", StringComparison.Ordinal)];
        var url = $"{managementBase}/runtime/webhooks/durabletask/instances/{instanceId}/raiseEvent/user_message";
        if (!string.IsNullOrEmpty(_durableSettings.ManagementKey))
            url += $"?code={Uri.EscapeDataString(_durableSettings.ManagementKey)}";

        // The steerable orchestrator awaits WaitForExternalEvent<string>("user_message"), so the event
        // body is the JSON-encoded reply string.
        var body = new StringContent(JsonSerializer.Serialize(text), Encoding.UTF8, "application/json");

        var http = _httpFactory.CreateClient();
        var response = await http.PostAsync(url, body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[InteractionEvent] failed to raise user_message on instance {Instance}: {Status} {Error}", instanceId, response.StatusCode, err);
        }
        else
        {
            _logger.LogInformation("[InteractionEvent] raised user_message on instance {Instance}", instanceId);
        }
    }
```

`SteerableInteractionReply` is in the `TectikaAgents.Api.Services` namespace, which `InteractionsController` already imports (`using TectikaAgents.Api.Services;`). `JsonSerializer`, `Encoding`, and `StringContent` are already imported.

- [ ] **Step 3: Build the API**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/InteractionsController.cs
git commit -m "feat(api): respond resumes steerable runs via user_message"
```

---

## Task 6: Resolve the pending interaction on a free-typed chat reply (API)

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/ChatService.cs`

- [ ] **Step 1: Resolve the pending steerable interaction when injecting**

In `ChatService.cs`, find the `active` branch inside `SendAsync` (lines 55-61):

```csharp
        if (active)
        {
            await EchoUserMessageAsync(run!.Id, taskId, run.CurrentStep, text, ct);
            await PostAsync(BuildUrl($"{run.DurableFunctionInstanceId}/message"), new { Text = text }, ct);
            _logger.LogInformation("[Chat] injected message into run {RunId} task {TaskId}", run.Id, taskId);
            return new ChatResult(run.Id, Injected: true);
        }
```

Replace it with:

```csharp
        if (active)
        {
            await EchoUserMessageAsync(run!.Id, taskId, run.CurrentStep, text, ct);
            await PostAsync(BuildUrl($"{run.DurableFunctionInstanceId}/message"), new { Text = text }, ct);
            // If the run was paused on a steerable request, a free-typed reply answers it — resolve the
            // record so it leaves the chat card, the Approvals tab, and the notification list.
            if (run.Status == RunStatus.AwaitingInteraction)
                await ResolvePendingSteerableInteractionAsync(tenantId, taskId, text, ct);
            _logger.LogInformation("[Chat] injected message into run {RunId} task {TaskId}", run.Id, taskId);
            return new ChatResult(run.Id, Injected: true);
        }
```

- [ ] **Step 2: Add the resolver method**

In the same class, add this private method (e.g. immediately after `EchoUserMessageAsync`, which ends around line 100):

```csharp
    private async Task ResolvePendingSteerableInteractionAsync(string tenantId, string taskId, string text, CancellationToken ct)
    {
        try
        {
            var pending = await _cosmos.GetPendingInteractionsAsync(tenantId, ct);
            var match = pending.FirstOrDefault(i =>
                i.TaskId == taskId && i.Origin == InteractionOrigin.Steerable && i.Status == InteractionStatus.Pending);
            if (match is null) return;

            match.Status = InteractionStatus.Responded;
            match.RespondedAt = DateTimeOffset.UtcNow;
            match.Answer = text;   // free-typed reply; Approval decision is left null (answered as text)
            await _cosmos.UpdateInteractionAsync(match, ct);
            _logger.LogInformation("[Chat] resolved steerable interaction {Id} via free-typed reply", match.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] failed to resolve pending steerable interaction for task {TaskId}", taskId);
        }
    }
```

`ICosmosDbService` already exposes `GetPendingInteractionsAsync(tenantId, ct)` and `UpdateInteractionAsync(...)` (used by `InteractionsController`). `System.Linq` is available (`FirstOrDefault`); `InteractionOrigin`/`InteractionStatus` are in `TectikaAgents.Core.Models`, already imported by this file. If the build reports `GetPendingInteractionsAsync` is not on `ICosmosDbService`, add its signature to `ICosmosDbService` (it exists on the concrete services) — but verify first; it is expected to be present.

- [ ] **Step 3: Build the API**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/ChatService.cs
git commit -m "feat(api): free-typed chat reply resolves the pending steerable request"
```

---

## Task 7: Surface the request in the chat + Enter-to-send (Web)

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`

- [ ] **Step 1: Add `origin` to the TS `HumanInteraction`**

In `src/web/tectika-board/src/lib/types.ts`, find the `HumanInteraction` interface (starts at line 281) and add this field after `id: string;` (the first field):

```ts
  origin?: 'Pipeline' | 'Steerable';
```

- [ ] **Step 2: Add imports to ItemPanel**

In `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`, find the existing import of `contextFromEvents` (added by the previous feature):

```tsx
import { contextFromEvents, sumTokens } from '@/lib/thinking-phrases';
```

Add the component import immediately after it:

```tsx
import { InteractionCard } from '@/components/InteractionCard';
```

Then add `HumanInteraction` to the **existing** type-import on line 7 (do not add a second `from '@/lib/types'` line, which would lint-error). Change:

```tsx
import type { Artifact, AgentTask, AgentRole, RunEvent } from '@/lib/types';
```
to:
```tsx
import type { Artifact, AgentTask, AgentRole, RunEvent, HumanInteraction } from '@/lib/types';
```

- [ ] **Step 3: Fetch the task's pending interaction in `AgentChat`**

In the `AgentChat` function, immediately after the line `const [justSent, setJustSent] = useState(false);`, add:

```tsx
  const [pendingInteraction, setPendingInteraction] = useState<HumanInteraction | null>(null);

  // When the run is paused on an agent request, load the pending interaction for this task so we can
  // render its card inline. Cleared as soon as the task is no longer awaiting.
  useEffect(() => {
    if (task.status !== 'AwaitingInteraction') {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- clear card when not awaiting
      setPendingInteraction(null);
      return;
    }
    let alive = true;
    api.interactions.pending()
      .then(list => { if (alive) setPendingInteraction(list.find(i => i.taskId === task.id) ?? null); })
      .catch(() => {});
    return () => { alive = false; };
  }, [task.status, task.id]);
```

- [ ] **Step 4: Render the card in the message list**

In `AgentChat`'s returned JSX, find the live-edge line inside the scrollable message area:

```tsx
        {working && <LiveEdge agentName={role?.displayName} context={liveContext} anchorAt={anchorAt} tokens={tokens} />}
        <div ref={endRef} />
```

Replace it with:

```tsx
        {pendingInteraction && (
          <InteractionCard
            interaction={pendingInteraction}
            onResponded={() => { setPendingInteraction(null); refreshTask(task.id); }}
          />
        )}
        {working && <LiveEdge agentName={role?.displayName} context={liveContext} anchorAt={anchorAt} tokens={tokens} />}
        <div ref={endRef} />
```

`refreshTask` is already destructured from `useBoard()` in `AgentChat` (used by the slash-command context).

- [ ] **Step 5: Enter-to-send / Shift+Enter newline**

In the textarea's `onKeyDown` handler, find the final send line:

```tsx
            if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); send(); }
```

Replace it with:

```tsx
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
```

(The slash-menu `Enter` handler earlier in the same `onKeyDown` already `return`s before reaching this line, so the command palette keeps priority; `Shift+Enter` falls through to the textarea's default newline.)

- [ ] **Step 6: Update the placeholder text**

Find the textarea `placeholder`:

```tsx
          rows={2} placeholder="Message the agent…  (/ for commands, ⌘/Ctrl + Enter to send)"
```

Replace with:

```tsx
          rows={2} placeholder="Message the agent…  (/ for commands, Enter to send, Shift+Enter for newline)"
```

- [ ] **Step 7: Typecheck + lint**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/src/web/tectika-board && npx tsc --noEmit && npx eslint src/components/workspace/ItemPanel.tsx src/lib/types.ts
```
Expected: tsc clean (exit 0); eslint reports no NEW issues (the pre-existing `AgentConfigEditor` ref errors + the `[task.id]` exhaustive-deps warning may remain — verify your edits add none).

- [ ] **Step 8: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): in-chat agent request card + Enter-to-send"
```

---

## Task 8: Full build + smoke verification

**Files:** none (verification only)

- [ ] **Step 1: Backend — full test suite + solution build**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
```
Expected: all tests pass (including the new `SteerableInteractionFactory` + `SteerableInteractionReply` tests).

- [ ] **Step 2: Web — production build**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/src/web/tectika-board && npm run build -- --webpack
```
Expected: build succeeds (TypeScript checked, all routes generated).

- [ ] **Step 3: Manual smoke (deployed — needs api + workflows + web deployed)**

Drive an agent to call `request_approval` (or `request_human_input`) in a chat, then verify:
1. A **notification** fires (bell).
2. The **Approvals tab** lists the request.
3. The **chat** shows the `InteractionCard` where the live edge would be.
4. **Approve from the chat** → run resumes (status → InProgress, live edge returns) and the entry disappears from the Approvals tab + notifications.
5. Trigger again → **Approve from the Approvals tab** → run resumes.
6. Trigger again → **free-type a reply in the chat** → run resumes and the Approvals entry clears.
7. **Enter** sends a chat message; **Shift+Enter** inserts a newline.

- [ ] **Step 4: Final commit (only if the smoke required a fix)**

```bash
git add -A && git commit -m "fix(steerable-hitl): smoke fixes"
```

---

## Notes for the implementer

- **Deploy footprint:** api + workflows + web (not frontend-only). The user merges/deploys manually.
- **Don't touch the old pipeline path** (`TaskPipelineOrchestrator`, `WriteInteractionActivity`, `ApprovalsController`) — `Origin` defaults to `Pipeline`, preserving all existing behavior.
- **Idempotency:** the interaction id is `{runId}-r{round}-interaction` + `UpsertInteractionAsync`, so a Durable activity retry replaces rather than duplicates.
- **Consistency:** an awaiting run can be answered three ways (chat card, Approvals tab, free-typed chat reply); all mark the record `Responded` and raise `user_message`. A rare double-answer is benign — the orchestrator consumes the first `user_message` and drains the second as next-round steering.
```
