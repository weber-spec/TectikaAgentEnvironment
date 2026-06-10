# Human Interaction System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a unified Human Interaction system so agents can pause the pipeline to present search results for selection, ask questions, or request approval — all handled through one `HumanInteraction` model and a type-aware frontend card.

**Architecture:** Agents embed `## INTERACTION_REQUIRED` JSON in their response; `InvokeAgentActivity` parses it and sets `StepResult.PendingInteraction`. The orchestrator detects this, calls `WriteInteractionActivity`, then waits on `WaitForExternalEvent("interaction-{step}")`. `InteractionsController` handles the user response, updates the `HumanInteraction` document, appends to `TaskBrief`, and raises the Durable Functions external event. The frontend `/interactions` page merges legacy approvals and new interactions, rendering each with a type-aware `InteractionCard`.

**Tech Stack:** .NET 8, C# 12, Azure Durable Functions, Azure Cosmos DB, Next.js 14, TypeScript, React 18

**Spec:** `docs/superpowers/specs/2026-06-09-human-interaction-design.md`

---

## File Map

**Create:**
- `src/core/TectikaAgents.Core/Models/HumanInteraction.cs`
- `src/workflows/Activities/WriteInteractionActivity.cs`
- `src/workflows/Activities/AppendTaskBriefActivity.cs`
- `src/api/TectikaAgents.Api/Controllers/InteractionsController.cs`
- `src/web/tectika-board/src/components/SearchResultItemCard.tsx`
- `src/web/tectika-board/src/components/InteractionCard.tsx`

**Modify:**
- `src/core/TectikaAgents.Core/Models/WorkflowRun.cs` — `RunStatus` + `StepResult`
- `src/core/TectikaAgents.Core/Models/AgentTask.cs` — `AgentTaskStatus` + `ArtifactSummary`
- `src/core/TectikaAgents.Core/Models/AgentEvent.cs` — `InteractionId` + event type constant
- `src/workflows/Activities/InvokeAgentActivity.cs` — parse `## INTERACTION_REQUIRED` + patch `ArtifactSummary`
- `src/workflows/Orchestrators/TaskPipelineOrchestrator.cs` — handle `PendingInteraction` pause/resume
- `src/workflows/Services/WorkflowCosmosService.cs` — add `CreateInteractionAsync` + `PatchTaskArtifactSummaryAsync`
- `src/workflows/Services/WorkflowEventPublisher.cs` — add `PublishInteractionRequiredAsync`
- `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs` — add `HumanInteraction` CRUD
- `src/api/TectikaAgents.Api/Services/CosmosDbService.cs` — implement `HumanInteraction` CRUD
- `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs` — implement `HumanInteraction` CRUD
- `src/web/tectika-board/src/lib/types.ts` — add types
- `src/web/tectika-board/src/lib/api.ts` — add `interactions` namespace
- `src/web/tectika-board/src/app/approvals/page.tsx` — merge interactions + approvals
- `src/web/tectika-board/src/components/board/TableView.tsx` — add `result` column

---

## Task 1: Core Models

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/HumanInteraction.cs`
- Modify: `src/core/TectikaAgents.Core/Models/WorkflowRun.cs`
- Modify: `src/core/TectikaAgents.Core/Models/AgentTask.cs`
- Modify: `src/core/TectikaAgents.Core/Models/AgentEvent.cs`

- [ ] **Step 1: Create HumanInteraction model**

Create `src/core/TectikaAgents.Core/Models/HumanInteraction.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class HumanInteraction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("stepIndex")]
    public int StepIndex { get; set; }

    [JsonPropertyName("type")]
    public InteractionType Type { get; set; }

    [JsonPropertyName("status")]
    public InteractionStatus Status { get; set; } = InteractionStatus.Pending;

    [JsonPropertyName("actionDescription")]
    public string ActionDescription { get; set; } = string.Empty;

    [JsonPropertyName("requestedFrom")]
    public List<string> RequestedFrom { get; set; } = [];

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("respondedBy")]
    public string? RespondedBy { get; set; }

    [JsonPropertyName("respondedAt")]
    public DateTimeOffset? RespondedAt { get; set; }

    // Selection fields
    [JsonPropertyName("items")]
    public List<SearchResultItem>? Items { get; set; }

    [JsonPropertyName("selectedIndex")]
    public int? SelectedIndex { get; set; }

    // Question fields
    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("questionOptions")]
    public List<string>? QuestionOptions { get; set; }

    [JsonPropertyName("answer")]
    public string? Answer { get; set; }

    // Approval fields
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("approved")]
    public bool? Approved { get; set; }

    [JsonPropertyName("identityToBeUsed")]
    public string? IdentityToBeUsed { get; set; }
}

public class SearchResultItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("details")]
    public List<string>? Details { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

// Embedded in StepResult to signal the orchestrator
public class PendingInteractionRequest
{
    public InteractionType Type { get; set; }
    public string ActionDescription { get; set; } = string.Empty;
    public List<SearchResultItem>? Items { get; set; }
    public string? Question { get; set; }
    public List<string>? QuestionOptions { get; set; }
}

// Payload raised as Durable Functions external event
public record InteractionResponsePayload(
    string InteractionId,
    string InteractionType,
    int? SelectedIndex,
    string? SelectedTitle,
    string? SelectedPrice,
    string? Answer,
    bool? Approved,
    string? Notes);

public enum InteractionType { Approval, Selection, Question }
public enum InteractionStatus { Pending, Responded, Expired }
```

- [ ] **Step 2: Add AwaitingInteraction to RunStatus and PendingInteraction to StepResult**

In `src/core/TectikaAgents.Core/Models/WorkflowRun.cs`, replace line 101:
```csharp
public enum RunStatus { Pending, Running, PausedApproval, Completed, Failed, Cancelled }
```
with:
```csharp
public enum RunStatus { Pending, Running, PausedApproval, AwaitingInteraction, Completed, Failed, Cancelled }
```

Add `PendingInteraction` to `StepResult` (after the `Error` property on line 86):
```csharp
    [JsonPropertyName("pendingInteraction")]
    public PendingInteractionRequest? PendingInteraction { get; set; }
```

- [ ] **Step 3: Add AwaitingInteraction to AgentTaskStatus and ArtifactSummary to AgentTask**

In `src/core/TectikaAgents.Core/Models/AgentTask.cs`, replace line 92:
```csharp
public enum AgentTaskStatus { Backlog, InProgress, AwaitingApproval, Blocked, Review, Done, Failed }
```
with:
```csharp
public enum AgentTaskStatus { Backlog, InProgress, AwaitingApproval, AwaitingInteraction, Blocked, Review, Done, Failed }
```

Add `ArtifactSummary` field to `AgentTask` (after `TaskBrief` on line 62):
```csharp
    [JsonPropertyName("artifactSummary")]
    public string? ArtifactSummary { get; set; }
```

- [ ] **Step 4: Add InteractionId and InteractionRequired to AgentEvent**

In `src/core/TectikaAgents.Core/Models/AgentEvent.cs`, add after `ApprovalId` (line 33):
```csharp
    [JsonPropertyName("interactionId")]
    public string? InteractionId { get; set; }

    [JsonPropertyName("interactionType")]
    public string? InteractionType { get; set; }
```

In the `Types` static class, add after `ApprovalRequired`:
```csharp
        public const string InteractionRequired = "interaction_required";
```

- [ ] **Step 5: Build core project**

```
cd src/core/TectikaAgents.Core && dotnet build
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 6: Commit**

```
git add src/core/TectikaAgents.Core/Models/
git commit -m "feat(core): add HumanInteraction model, AwaitingInteraction status, ArtifactSummary field"
```

---

## Task 2: Data Access Layer

**Files:**
- Modify: `src/workflows/Services/WorkflowCosmosService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs`

- [ ] **Step 1: Add CreateInteractionAsync and PatchTaskArtifactSummaryAsync to WorkflowCosmosService**

In `src/workflows/Services/WorkflowCosmosService.cs`, add after the `CreateApprovalAsync` method:

```csharp
    // ── HumanInteraction ──────────────────────────────────────────────────────

    public async Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await C("humanInteractions").CreateItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }
```

Add after `PatchTaskBriefAsync`:
```csharp
    public async Task PatchTaskArtifactSummaryAsync(string boardId, string taskId, string summary, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation> { PatchOperation.Set("/artifactSummary", summary) };
        await C("tasks").PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId), patchOps, cancellationToken: ct);
    }
```

- [ ] **Step 2: Add HumanInteraction methods to ICosmosDbService**

In `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs`, add after the `// ── Approvals` section:

```csharp
    // ── Human Interactions ─────────────────────────────────────────────────────
    Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default);
    Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default);
    Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default);
    Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default);
```

- [ ] **Step 3: Implement in CosmosDbService**

In `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`:

Add container constant after `AuditLogContainer`:
```csharp
    public const string HumanInteractionsContainer = "humanInteractions";
```

Add container to `EnsureInfrastructureAsync` containers array:
```csharp
            (HumanInteractionsContainer, "/runId"),
```

Add after the Approvals region:
```csharp
    // ── Human Interactions ─────────────────────────────────────────────────────

    public async Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await GetContainer(HumanInteractionsContainer).CreateItemAsync(interaction, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default)
    {
        try
        {
            var res = await GetContainer(HumanInteractionsContainer).ReadItemAsync<HumanInteraction>(interactionId, new PartitionKey(runId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        var res = await GetContainer(HumanInteractionsContainer).ReplaceItemAsync(interaction, interaction.Id, new PartitionKey(interaction.RunId), cancellationToken: ct);
        return res.Resource;
    }

    public async Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = 'Pending'")
            .WithParameter("@tenantId", tenantId);
        return await QueryAsync<HumanInteraction>(HumanInteractionsContainer, query, null, ct);
    }
```

- [ ] **Step 4: Implement in InMemoryCosmosDbService**

In `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs`:

Add dictionary field after `_approvals`:
```csharp
    private readonly ConcurrentDictionary<string, HumanInteraction> _interactions = new();
```

Add methods after the Approvals region:
```csharp
    // ── Human Interactions ──────────────────────────────────────────────────────
    public Task<HumanInteraction> CreateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        _interactions[interaction.Id] = interaction;
        return Task.FromResult(interaction);
    }

    public Task<HumanInteraction?> GetInteractionAsync(string runId, string interactionId, CancellationToken ct = default) =>
        Task.FromResult(_interactions.TryGetValue(interactionId, out var i) && i.RunId == runId ? i : null);

    public Task<HumanInteraction> UpdateInteractionAsync(HumanInteraction interaction, CancellationToken ct = default)
    {
        _interactions[interaction.Id] = interaction;
        return Task.FromResult(interaction);
    }

    public Task<IEnumerable<HumanInteraction>> GetPendingInteractionsAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_interactions.Values
            .Where(i => i.TenantId == tenantId && i.Status == InteractionStatus.Pending)
            .AsEnumerable());
```

- [ ] **Step 5: Build API project**

```
cd src/api/TectikaAgents.Api && dotnet build
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 6: Commit**

```
git add src/workflows/Services/WorkflowCosmosService.cs
git add src/api/TectikaAgents.Api/Services/
git commit -m "feat(data): add HumanInteraction CRUD to CosmosDbService and InMemory"
```

---

## Task 3: Workflow Infrastructure

**Files:**
- Modify: `src/workflows/Services/WorkflowEventPublisher.cs`
- Create: `src/workflows/Activities/WriteInteractionActivity.cs`
- Create: `src/workflows/Activities/AppendTaskBriefActivity.cs`

- [ ] **Step 1: Add PublishInteractionRequiredAsync to WorkflowEventPublisher**

In `src/workflows/Services/WorkflowEventPublisher.cs`, add after `PublishApprovalRequiredAsync`:

```csharp
    public Task PublishInteractionRequiredAsync(string runId, string taskId, int step, string interactionId, string interactionType, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent
        {
            Type = AgentEvent.Types.InteractionRequired,
            RunId = runId,
            TaskId = taskId,
            Step = step,
            InteractionId = interactionId,
            InteractionType = interactionType
        }, ct);
```

- [ ] **Step 2: Create WriteInteractionActivity**

Create `src/workflows/Activities/WriteInteractionActivity.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class WriteInteractionActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<WriteInteractionActivity> _logger;

    public WriteInteractionActivity(WorkflowCosmosService cosmos, WorkflowEventPublisher events, ILogger<WriteInteractionActivity> logger)
    {
        _cosmos = cosmos;
        _events = events;
        _logger = logger;
    }

    [Function(nameof(WriteInteractionActivity))]
    public async Task<string> Run([ActivityTrigger] WriteInteractionInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        var interaction = new HumanInteraction
        {
            RunId             = input.RunId,
            TaskId            = input.TaskId,
            BoardId           = input.BoardId,
            TenantId          = input.TenantId,
            StepIndex         = input.StepIndex,
            Type              = input.Pending.Type,
            ActionDescription = input.Pending.ActionDescription,
            RequestedFrom     = input.Approvers,
            Items             = input.Pending.Items,
            Question          = input.Pending.Question,
            QuestionOptions   = input.Pending.QuestionOptions,
            ExpiresAt         = DateTimeOffset.UtcNow.AddHours(48)
        };

        var saved = await _cosmos.CreateInteractionAsync(interaction, ct);

        _logger.LogInformation("Interaction {Id} ({Type}) created for run {RunId} step {Step}",
            saved.Id, saved.Type, input.RunId, input.StepIndex);

        await _events.PublishInteractionRequiredAsync(
            input.RunId, input.TaskId, input.StepIndex, saved.Id, saved.Type.ToString(), ct);

        return saved.Id;
    }
}

public record WriteInteractionInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    int StepIndex,
    List<string> Approvers,
    PendingInteractionRequest Pending);
```

- [ ] **Step 3: Create AppendTaskBriefActivity**

Create `src/workflows/Activities/AppendTaskBriefActivity.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class AppendTaskBriefActivity
{
    private readonly WorkflowCosmosService _cosmos;

    public AppendTaskBriefActivity(WorkflowCosmosService cosmos) => _cosmos = cosmos;

    [Function(nameof(AppendTaskBriefActivity))]
    public async Task Run([ActivityTrigger] AppendTaskBriefInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct);
        if (task is null) return;
        task.TaskBrief += $"\n{input.AppendText}";
        await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
    }
}

public record AppendTaskBriefInput(string BoardId, string TaskId, string AppendText);
```

- [ ] **Step 4: Build workflows project**

```
cd src/workflows && dotnet build
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 5: Commit**

```
git add src/workflows/
git commit -m "feat(workflows): add WriteInteractionActivity, AppendTaskBriefActivity, interaction event"
```

---

## Task 4: InvokeAgentActivity — Parse INTERACTION_REQUIRED

**Files:**
- Modify: `src/workflows/Activities/InvokeAgentActivity.cs`

- [ ] **Step 1: Add ParseInteractionSection to InvokeAgentActivity**

In `src/workflows/Activities/InvokeAgentActivity.cs`, extend the existing `ParseAgentSections` method.

Replace the current `ParseAgentSections` private method (lines 133–158) with:

```csharp
    private static (string Brief, string Summary, PendingInteractionRequest? Interaction, string CleanContent) ParseAgentSections(string content)
    {
        string ExtractFirstNonEmptyLine(string marker)
        {
            var idx = content.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            return content[(idx + marker.Length)..]
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        }

        var brief = ExtractFirstNonEmptyLine("## Brief Update");

        var summary = "";
        var summaryIdx = content.LastIndexOf("## Artifact Summary", StringComparison.OrdinalIgnoreCase);
        if (summaryIdx >= 0)
        {
            var jsonStart = content.IndexOf('{', summaryIdx);
            var jsonEnd   = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                summary = content[jsonStart..(jsonEnd + 1)];
        }

        PendingInteractionRequest? interaction = null;
        var cleanContent = content;
        var interactionMarker = "## INTERACTION_REQUIRED";
        var interactionIdx = content.LastIndexOf(interactionMarker, StringComparison.OrdinalIgnoreCase);
        if (interactionIdx >= 0)
        {
            var jsonStart = content.IndexOf('{', interactionIdx);
            // Find matching closing brace (accounting for nested objects)
            if (jsonStart >= 0)
            {
                var depth = 0;
                var jsonEnd = -1;
                for (var k = jsonStart; k < content.Length; k++)
                {
                    if (content[k] == '{') depth++;
                    else if (content[k] == '}') { depth--; if (depth == 0) { jsonEnd = k; break; } }
                }
                if (jsonEnd > jsonStart)
                {
                    var json = content[jsonStart..(jsonEnd + 1)];
                    try
                    {
                        interaction = System.Text.Json.JsonSerializer.Deserialize<PendingInteractionRequest>(json,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch { /* malformed JSON — ignore */ }
                    // Strip the INTERACTION_REQUIRED section from content stored as artifact
                    cleanContent = content[..interactionIdx].TrimEnd();
                }
            }
        }

        return (brief, summary, interaction, cleanContent);
    }
```

- [ ] **Step 2: Update the call site in Run() and use CleanContent + PendingInteraction**

Replace the two lines in `Run()` that call `ParseAgentSections`:

Old (line 76):
```csharp
        var (briefUpdate, artifactSummary) = ParseAgentSections(result.Content);
```

New:
```csharp
        var (briefUpdate, artifactSummary, pendingInteraction, cleanContent) = ParseAgentSections(result.Content);
```

Replace `Content = result.Content` in the artifact creation (line 91) with:
```csharp
            Content     = cleanContent,
```

- [ ] **Step 3: Patch ArtifactSummary on the task after saving artifact**

After `await _cosmos.PatchTaskBriefAsync(...)` (line 115), add:
```csharp
        if (!string.IsNullOrEmpty(artifactSummary))
        {
            // Extract plain summary text from JSON if wrapped
            var summaryText = artifactSummary;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(artifactSummary);
                if (doc.RootElement.TryGetProperty("summary", out var s))
                    summaryText = s.GetString() ?? summaryText;
            }
            catch { /* use raw */ }
            await _cosmos.PatchTaskArtifactSummaryAsync(input.BoardId, input.TaskId, summaryText, ct);
        }
```

- [ ] **Step 4: Return PendingInteraction in StepResult**

Replace the `return new StepResult { ... }` block (lines 121–130) with:

```csharp
        return new StepResult
        {
            Step               = input.Step,
            Status             = RunStatus.Completed,
            FoundryRunId       = result.CompletionId,
            ArtifactId         = savedArtifact.Id,
            TokenUsage         = usage,
            DurationMs         = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
            CompletedAt        = DateTimeOffset.UtcNow,
            PendingInteraction = pendingInteraction
        };
```

- [ ] **Step 5: Build workflows project**

```
cd src/workflows && dotnet build
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 6: Commit**

```
git add src/workflows/Activities/InvokeAgentActivity.cs
git commit -m "feat(agent): parse ## INTERACTION_REQUIRED section, strip from artifact, patch ArtifactSummary"
```

---

## Task 5: Orchestrator — Interaction Pause/Resume

**Files:**
- Modify: `src/workflows/Orchestrators/TaskPipelineOrchestrator.cs`

- [ ] **Step 1: Add interaction pause/resume block after the AgentExecution step**

In `TaskPipelineOrchestrator.cs`, after the existing `WriteAuditActivity` call (the last line in the agent step block, after both `UpdateRunStatusActivity` and `WriteAuditActivity`), add:

```csharp
            // ── Interaction Gate (agent-requested) ────────────────────────────
            if (stepResult.PendingInteraction is not null)
            {
                logger.LogInformation("Interaction gate at step {Step} type={Type}", step.Step, stepResult.PendingInteraction.Type);

                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.AwaitingInteraction, step.Step));

                var interactionId = await context.CallActivityAsync<string>(
                    nameof(WriteInteractionActivity),
                    new WriteInteractionInput(
                        input.RunId,
                        input.TaskId,
                        input.BoardId,
                        input.TenantId,
                        step.Step,
                        step.Approvers,
                        stepResult.PendingInteraction));

                var response = await context.WaitForExternalEvent<InteractionResponsePayload>(
                    $"interaction-{step.Step}",
                    TimeSpan.FromHours(48));

                // Format task brief entry
                var briefEntry = response.InteractionType switch
                {
                    "Selection" => $"[Human, {response.InteractionId[..Math.Min(6, response.InteractionId.Length)]}, Selection]: Selected \"{response.SelectedTitle}\" — {response.SelectedPrice}",
                    "Question"  => $"[Human, {response.InteractionId[..Math.Min(6, response.InteractionId.Length)]}, Question]: \"{response.Answer}\"",
                    _           => $"[Human, {response.InteractionId[..Math.Min(6, response.InteractionId.Length)]}, Approval]: {(response.Approved == true ? "Approved" : "Rejected")}{(string.IsNullOrEmpty(response.Notes) ? "" : $" — {response.Notes}")}",
                };

                await context.CallActivityAsync(nameof(AppendTaskBriefActivity),
                    new AppendTaskBriefInput(input.BoardId, input.TaskId, briefEntry));

                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Running, step.Step));
            }
```

Also add the required `using` statement at the top if not already present:
```csharp
using TectikaAgents.Workflows.Activities;
```

- [ ] **Step 2: Build workflows project**

```
cd src/workflows && dotnet build
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 3: Commit**

```
git add src/workflows/Orchestrators/TaskPipelineOrchestrator.cs
git commit -m "feat(orchestrator): pause pipeline on PendingInteraction, resume on InteractionResponsePayload"
```

---

## Task 6: InteractionsController

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/InteractionsController.cs`

- [ ] **Step 1: Create InteractionsController**

Create `src/api/TectikaAgents.Api/Controllers/InteractionsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/interactions")]
[Authorize]
public class InteractionsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<InteractionsController> _logger;

    public InteractionsController(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<InteractionsController> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(CancellationToken ct) =>
        Ok(await _cosmos.GetPendingInteractionsAsync(TenantId, ct));

    [HttpPost("{interactionId}/respond")]
    public async Task<IActionResult> Respond(string interactionId, [FromBody] InteractionRespond req, CancellationToken ct)
    {
        var interaction = await _cosmos.GetInteractionAsync(req.RunId, interactionId, ct);
        if (interaction is null) return NotFound();
        if (interaction.Status != InteractionStatus.Pending) return Conflict("Interaction already resolved.");

        // Persist response
        interaction.Status      = InteractionStatus.Responded;
        interaction.RespondedBy = UserId;
        interaction.RespondedAt = DateTimeOffset.UtcNow;

        switch (interaction.Type)
        {
            case InteractionType.Selection:
                if (req.SelectedIndex is null || req.SelectedIndex < 0 || req.SelectedIndex >= (interaction.Items?.Count ?? 0))
                    return BadRequest("Invalid selectedIndex.");
                interaction.SelectedIndex = req.SelectedIndex;
                break;

            case InteractionType.Question:
                if (string.IsNullOrWhiteSpace(req.Answer)) return BadRequest("Answer is required.");
                interaction.Answer = req.Answer;
                break;

            case InteractionType.Approval:
                if (req.Approved is null) return BadRequest("Approved field is required.");
                interaction.Approved = req.Approved;
                interaction.Notes    = req.Notes;
                break;
        }

        await _cosmos.UpdateInteractionAsync(interaction, ct);

        // Build response payload for orchestrator
        var selectedItem = interaction.Type == InteractionType.Selection && interaction.SelectedIndex.HasValue
            ? interaction.Items?[interaction.SelectedIndex.Value]
            : null;

        var payload = new InteractionResponsePayload(
            interactionId,
            interaction.Type.ToString(),
            interaction.SelectedIndex,
            selectedItem?.Title,
            selectedItem?.Price,
            interaction.Answer,
            interaction.Approved,
            interaction.Notes);

        // Wake up orchestrator
        var run = await _cosmos.GetRunAsync(interaction.TaskId, req.RunId, ct);
        if (run?.DurableFunctionInstanceId is not null)
            await RaiseInteractionEventAsync(run.DurableFunctionInstanceId, interaction.StepIndex, payload, ct);
        else
            _logger.LogWarning("No DurableFunctionInstanceId for run {RunId}", req.RunId);

        // Update task status back to InProgress
        var task = await _cosmos.GetTaskAsync(interaction.BoardId, interaction.TaskId, ct);
        if (task is not null)
        {
            task.Status = AgentTaskStatus.InProgress;
            await _cosmos.UpdateTaskAsync(task, ct);
        }

        _logger.LogInformation("Interaction {Id} ({Type}) responded by {User}", interactionId, interaction.Type, UserId);
        return Ok(interaction);
    }

    private async Task RaiseInteractionEventAsync(
        string instanceId, int stepIndex, InteractionResponsePayload payload, CancellationToken ct)
    {
        var baseUrl = _config["DurableFunctions:StartUrl"]
            ?? "http://localhost:7071/api/pipelines/start";

        var managementBase = baseUrl[..baseUrl.IndexOf("/api/", StringComparison.Ordinal)];
        var eventName = $"interaction-{stepIndex}";
        var url = $"{managementBase}/runtime/webhooks/durabletask/instances/{instanceId}/raiseEvent/{eventName}";

        var body = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var http = _httpFactory.CreateClient();
        var response = await http.PostAsync(url, body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Failed to raise interaction event: {Status} {Error}", response.StatusCode, err);
        }
        else
        {
            _logger.LogInformation("Raised interaction event '{Event}' on instance {Instance}", eventName, instanceId);
        }
    }
}

public record InteractionRespond(
    string RunId,
    int? SelectedIndex,
    string? Answer,
    bool? Approved,
    string? Notes);
```

- [ ] **Step 2: Build API project**

```
cd src/api/TectikaAgents.Api && dotnet build
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 3: Verify endpoint appears**

Start API locally and check: `curl http://localhost:5138/api/interactions/pending`
Expected: `[]` (empty array, 200 OK)

- [ ] **Step 4: Commit**

```
git add src/api/TectikaAgents.Api/Controllers/InteractionsController.cs
git commit -m "feat(api): add InteractionsController with GET pending and POST respond"
```

---

## Task 7: Frontend Types and API Client

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/lib/api.ts`

- [ ] **Step 1: Add HumanInteraction types to types.ts**

In `src/web/tectika-board/src/lib/types.ts`:

After line 11 (`export type ApprovalStatus = ...`), add:
```typescript
export type InteractionType = 'Approval' | 'Selection' | 'Question';
export type InteractionStatus = 'Pending' | 'Responded' | 'Expired';
```

Replace line 11 `RunStatus`:
```typescript
export type RunStatus = 'Pending' | 'Running' | 'PausedApproval' | 'AwaitingInteraction' | 'Completed' | 'Failed' | 'Cancelled';
```

Replace `AgentTaskStatus` on line 6:
```typescript
export type AgentTaskStatus = 'Backlog' | 'InProgress' | 'AwaitingApproval' | 'AwaitingInteraction' | 'Blocked' | 'Review' | 'Done' | 'Failed';
```

Add `artifactSummary` to `AgentTask` interface (after `taskBrief`):
```typescript
  artifactSummary?: string;
```

Add `HumanInteraction` and `SearchResultItem` interfaces (after the `Approval` interface at line 188):
```typescript
export interface SearchResultItem {
  title: string;
  subtitle?: string;
  price?: string;
  details?: string[];
  link?: string;
  imageUrl?: string;
  metadata?: Record<string, string>;
}

export interface HumanInteraction {
  id: string;
  tenantId: string;
  runId: string;
  taskId: string;
  boardId: string;
  stepIndex: number;
  type: InteractionType;
  status: InteractionStatus;
  actionDescription: string;
  requestedFrom: string[];
  requestedAt: string;
  expiresAt: string;
  respondedBy?: string;
  respondedAt?: string;
  // Selection
  items?: SearchResultItem[];
  selectedIndex?: number;
  // Question
  question?: string;
  questionOptions?: string[];
  answer?: string;
  // Approval
  notes?: string;
  approved?: boolean;
  identityToBeUsed?: string;
}
```

- [ ] **Step 2: Add interactions namespace to api.ts**

In `src/web/tectika-board/src/lib/api.ts`, add `HumanInteraction` to the top import:
```typescript
import type {
  Board, AgentTask, AgentRole, Artifact, Approval, WorkflowRun, AgentEvent, HumanInteraction,
} from './types';
```

Add `interactions` namespace after the `approvals` namespace:
```typescript
  interactions: {
    pending: () => fetchApi<HumanInteraction[]>('/api/interactions/pending'),
    respond: (
      interactionId: string,
      runId: string,
      opts: { selectedIndex?: number; answer?: string; approved?: boolean; notes?: string }
    ) =>
      fetchApi<HumanInteraction>(`/api/interactions/${interactionId}/respond`, {
        method: 'POST',
        body: JSON.stringify({ runId, ...opts }),
      }),
  },
```

- [ ] **Step 3: Commit**

```
git add src/web/tectika-board/src/lib/
git commit -m "feat(frontend): add HumanInteraction types and api.interactions client"
```

---

## Task 8: SearchResultItemCard Component

**Files:**
- Create: `src/web/tectika-board/src/components/SearchResultItemCard.tsx`

- [ ] **Step 1: Create component**

Create `src/web/tectika-board/src/components/SearchResultItemCard.tsx`:

```tsx
import type { SearchResultItem } from '@/lib/types';

interface Props {
  item: SearchResultItem;
  selected: boolean;
  onSelect: () => void;
  disabled?: boolean;
}

export function SearchResultItemCard({ item, selected, onSelect, disabled }: Props) {
  return (
    <button
      type="button"
      onClick={onSelect}
      disabled={disabled}
      className={[
        'w-full text-left rounded-xl border p-4 transition-all',
        'focus:outline-none focus-visible:ring-2 focus-visible:ring-[#a25ddc]',
        selected
          ? 'border-[#a25ddc] bg-[#a25ddc11]'
          : 'border-[var(--border)] bg-[var(--background)] hover:border-[var(--muted)]',
        disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer',
      ].join(' ')}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-start gap-3 flex-1 min-w-0">
          {/* Radio indicator */}
          <span
            className={[
              'mt-0.5 w-4 h-4 rounded-full border-2 shrink-0 flex items-center justify-center',
              selected ? 'border-[#a25ddc]' : 'border-[var(--muted)]',
            ].join(' ')}
          >
            {selected && <span className="w-2 h-2 rounded-full bg-[#a25ddc]" />}
          </span>

          <div className="flex-1 min-w-0">
            <p className="text-sm font-semibold text-[var(--foreground)] truncate">{item.title}</p>
            {item.subtitle && (
              <p className="text-xs text-[var(--muted)] mt-0.5">{item.subtitle}</p>
            )}
            {item.details && item.details.length > 0 && (
              <div className="flex flex-wrap gap-1 mt-2">
                {item.details.map((d, i) => (
                  <span key={i} className="text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] border border-[var(--border)]">
                    {d}
                  </span>
                ))}
              </div>
            )}
          </div>
        </div>

        <div className="text-right shrink-0">
          {item.price && (
            <p className="text-sm font-bold text-[var(--foreground)]">{item.price}</p>
          )}
          {item.link && (
            <a
              href={item.link}
              target="_blank"
              rel="noopener noreferrer"
              onClick={e => e.stopPropagation()}
              className="text-[10px] text-[#a25ddc] hover:underline mt-0.5 inline-block"
            >
              View ↗
            </a>
          )}
        </div>
      </div>
    </button>
  );
}
```

- [ ] **Step 2: Commit**

```
git add src/web/tectika-board/src/components/SearchResultItemCard.tsx
git commit -m "feat(ui): add SearchResultItemCard component"
```

---

## Task 9: InteractionCard Component

**Files:**
- Create: `src/web/tectika-board/src/components/InteractionCard.tsx`

- [ ] **Step 1: Create component**

Create `src/web/tectika-board/src/components/InteractionCard.tsx`:

```tsx
'use client';

import { useState } from 'react';
import type { HumanInteraction } from '@/lib/types';
import { SearchResultItemCard } from './SearchResultItemCard';
import { Button, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { relativeTime, displayName, daysUntil } from '@/lib/format';
import { colorFor } from '@/lib/palette';

interface Props {
  interaction: HumanInteraction;
  onRespond: (opts: { selectedIndex?: number; answer?: string; approved?: boolean; notes?: string }) => Promise<void>;
  busy: boolean;
}

export function InteractionCard({ interaction, onRespond, busy }: Props) {
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [answer, setAnswer] = useState('');
  const [notes, setNotes] = useState('');
  const expiry = daysUntil(interaction.expiresAt);

  const canSubmit = !busy && (
    (interaction.type === 'Selection' && selectedIndex !== null) ||
    (interaction.type === 'Question' && answer.trim().length > 0) ||
    interaction.type === 'Approval'
  );

  const handleApproval = (approved: boolean) =>
    onRespond({ approved, notes: notes.trim() || undefined });

  const handleSelection = () =>
    onRespond({ selectedIndex: selectedIndex! });

  const handleQuestion = () =>
    onRespond({ answer: answer.trim() });

  const handleQuestionOption = (opt: string) =>
    onRespond({ answer: opt });

  return (
    <div
      className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 flex flex-col gap-3"
      style={{ borderLeft: '4px solid #a25ddc' }}
    >
      {/* Header */}
      <div className="flex items-start gap-3">
        <span className="w-9 h-9 rounded-lg bg-[#a25ddc22] text-[#a25ddc] flex items-center justify-center shrink-0">
          {interaction.type === 'Selection' ? <Icon.list size={18} /> :
           interaction.type === 'Question'  ? <Icon.chat size={18} /> :
                                              <Icon.warning size={18} />}
        </span>
        <div className="flex-1 min-w-0">
          <p className="text-sm font-semibold text-[var(--foreground)]">{interaction.actionDescription}</p>
          <div className="flex items-center gap-3 mt-1.5 text-[11px] text-[var(--muted)] flex-wrap">
            <span className="inline-flex items-center gap-1">
              <Icon.clock size={12} /> requested {relativeTime(interaction.requestedAt)}
            </span>
            {expiry != null && (
              <span className={expiry <= 1 ? 'text-[#e2445c]' : ''}>expires in {expiry}d</span>
            )}
            {interaction.identityToBeUsed && (
              <span className="inline-flex items-center gap-1">
                <Icon.user size={12} /> as {displayName(interaction.identityToBeUsed)}
              </span>
            )}
          </div>
        </div>
        <div className="flex items-center -space-x-2">
          {interaction.requestedFrom.map(p => (
            <Avatar key={p} name={displayName(p)} hex={colorFor(p)} size={26} ring />
          ))}
        </div>
      </div>

      {/* Type-specific body */}
      {interaction.type === 'Selection' && interaction.items && (
        <div className="flex flex-col gap-2">
          {interaction.items.map((item, i) => (
            <SearchResultItemCard
              key={i}
              item={item}
              selected={selectedIndex === i}
              onSelect={() => setSelectedIndex(i)}
              disabled={busy}
            />
          ))}
          <div className="flex justify-end mt-1">
            <Button
              variant="primary"
              size="sm"
              disabled={!canSubmit}
              onClick={handleSelection}
            >
              <Icon.check size={15} /> Confirm Selection
            </Button>
          </div>
        </div>
      )}

      {interaction.type === 'Question' && (
        <div className="flex flex-col gap-2">
          {interaction.question && (
            <p className="text-sm text-[var(--foreground)]">{interaction.question}</p>
          )}
          {interaction.questionOptions && interaction.questionOptions.length > 0 ? (
            <div className="flex flex-col gap-1.5">
              {interaction.questionOptions.map(opt => (
                <button
                  key={opt}
                  type="button"
                  disabled={busy}
                  onClick={() => handleQuestionOption(opt)}
                  className="w-full text-left text-sm px-3 py-2 rounded-lg border border-[var(--border)] hover:border-[#a25ddc] hover:bg-[#a25ddc11] transition-colors disabled:opacity-50"
                >
                  {opt}
                </button>
              ))}
            </div>
          ) : (
            <div className="flex gap-2">
              <textarea
                className="flex-1 text-sm rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 resize-none focus:outline-none focus:border-[#a25ddc]"
                rows={3}
                placeholder="Your answer..."
                value={answer}
                onChange={e => setAnswer(e.target.value)}
                disabled={busy}
              />
              <Button
                variant="primary"
                size="sm"
                disabled={!canSubmit}
                onClick={handleQuestion}
                className="self-end"
              >
                <Icon.send size={15} /> Send
              </Button>
            </div>
          )}
        </div>
      )}

      {interaction.type === 'Approval' && (
        <div className="flex flex-col gap-2">
          <textarea
            className="text-sm rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 resize-none focus:outline-none focus:border-[#a25ddc]"
            rows={2}
            placeholder="Optional notes..."
            value={notes}
            onChange={e => setNotes(e.target.value)}
            disabled={busy}
          />
          <div className="flex items-center justify-end gap-2">
            <Button variant="danger" size="sm" disabled={busy} onClick={() => handleApproval(false)}>
              <Icon.x size={15} /> Reject
            </Button>
            <Button variant="primary" size="sm" disabled={busy} onClick={() => handleApproval(true)}>
              <Icon.check size={15} /> Approve
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 2: Commit**

```
git add src/web/tectika-board/src/components/InteractionCard.tsx
git commit -m "feat(ui): add InteractionCard with Approval/Selection/Question variants"
```

---

## Task 10: Interactions Page

**Files:**
- Modify: `src/web/tectika-board/src/app/approvals/page.tsx`

- [ ] **Step 1: Replace approvals page with merged interactions page**

Replace the entire content of `src/web/tectika-board/src/app/approvals/page.tsx` with:

```tsx
'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { Approval, HumanInteraction } from '@/lib/types';
import { InteractionCard } from '@/components/InteractionCard';
import { Button, Skeleton, EmptyState, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { relativeTime, displayName, daysUntil } from '@/lib/format';
import { colorFor } from '@/lib/palette';
import { toast } from '@/lib/toast';

export default function ApprovalsPage() {
  const [interactions, setInteractions] = useState<HumanInteraction[] | null>(null);
  const [approvals, setApprovals] = useState<Approval[]>([]);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([
      api.interactions.pending().catch(() => [] as HumanInteraction[]),
      api.approvals.pending().catch(() => [] as Approval[]),
    ]).then(([ints, apps]) => {
      setInteractions(ints);
      setApprovals(apps);
    });
  }, []);

  const respondInteraction = async (
    i: HumanInteraction,
    opts: { selectedIndex?: number; answer?: string; approved?: boolean; notes?: string }
  ) => {
    setBusy(i.id);
    try {
      await api.interactions.respond(i.id, i.runId, opts);
      setInteractions(prev => (prev ?? []).filter(x => x.id !== i.id));
      toast('Response submitted', 'success');
    } catch { toast('Could not submit response', 'error'); }
    finally { setBusy(null); }
  };

  const respondApproval = async (a: Approval, approved: boolean) => {
    setBusy(a.id);
    try {
      await api.approvals.respond(a.id, a.runId, approved);
      setApprovals(prev => prev.filter(x => x.id !== a.id));
      toast(approved ? 'Approved' : 'Rejected', approved ? 'success' : 'info');
    } catch { toast('Could not submit decision', 'error'); }
    finally { setBusy(null); }
  };

  const loading = interactions === null;
  const totalCount = (interactions?.length ?? 0) + approvals.length;

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Interactions</h1>
        <p className="text-sm text-[var(--muted)] mt-0.5">
          Agent requests waiting for your input — selections, questions, and approvals.
        </p>
      </div>
      <div className="px-8 pb-8 flex-1 max-w-3xl">
        {loading ? (
          <div className="flex flex-col gap-3">
            {[...Array(3)].map((_, i) => <Skeleton key={i} className="h-28" />)}
          </div>
        ) : totalCount === 0 ? (
          <EmptyState
            icon={<Icon.approvals size={48} />}
            title="Inbox zero"
            description="No interactions are waiting. Agent requests for selection, questions, or approvals will appear here."
          />
        ) : (
          <div className="flex flex-col gap-3">
            {/* New interaction-type items */}
            {(interactions ?? []).map(i => (
              <InteractionCard
                key={i.id}
                interaction={i}
                onRespond={opts => respondInteraction(i, opts)}
                busy={busy === i.id}
              />
            ))}

            {/* Legacy approval items */}
            {approvals.map(a => {
              const expiry = daysUntil(a.expiresAt);
              return (
                <div
                  key={a.id}
                  className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 flex flex-col gap-3"
                  style={{ borderLeft: '4px solid #a25ddc' }}
                >
                  <div className="flex items-start gap-3">
                    <span className="w-9 h-9 rounded-lg bg-[#a25ddc22] text-[#a25ddc] flex items-center justify-center shrink-0">
                      <Icon.warning size={18} />
                    </span>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-semibold text-[var(--foreground)]">{a.actionDescription}</p>
                      <div className="flex items-center gap-3 mt-1.5 text-[11px] text-[var(--muted)] flex-wrap">
                        <span className="inline-flex items-center gap-1"><Icon.clock size={12} /> requested {relativeTime(a.requestedAt)}</span>
                        {expiry != null && <span className={expiry <= 1 ? 'text-[#e2445c]' : ''}>expires in {expiry}d</span>}
                        {a.identityToBeUsed && <span className="inline-flex items-center gap-1"><Icon.user size={12} /> as {displayName(a.identityToBeUsed)}</span>}
                      </div>
                    </div>
                    <div className="flex items-center -space-x-2">
                      {a.requestedFrom.map(p => <Avatar key={p} name={displayName(p)} hex={colorFor(p)} size={26} ring />)}
                    </div>
                  </div>
                  <div className="flex items-center justify-end gap-2">
                    <Button variant="danger" size="sm" disabled={busy === a.id} onClick={() => respondApproval(a, false)}><Icon.x size={15} /> Reject</Button>
                    <Button variant="primary" size="sm" disabled={busy === a.id} onClick={() => respondApproval(a, true)}><Icon.check size={15} /> Approve</Button>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Commit**

```
git add src/web/tectika-board/src/app/approvals/page.tsx
git commit -m "feat(ui): upgrade interactions page to handle Selection/Question/Approval types"
```

---

## Task 11: Result Column in Table View

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/components/board/TableView.tsx` (or equivalent table component)

- [ ] **Step 1: Add 'result' to ColumnKind**

In `src/web/tectika-board/src/lib/types.ts`, add `'result'` to the `ColumnKind` union type:

```typescript
export type ColumnKind =
  | 'title' | 'status' | 'priority' | 'people' | 'date' | 'timeline'
  | 'number' | 'text' | 'tags' | 'dropdown' | 'progress' | 'rating'
  | 'checkbox' | 'link' | 'dependency' | 'upstream' | 'downstream' | 'tokens' | 'cost' | 'trigger'
  | 'createdAt' | 'lastUpdated' | 'itemId' | 'autoNumber' | 'formula' | 'result';
```

- [ ] **Step 2: Find and update the table cell renderer**

First find the table view file:
```
grep -r "ColumnKind\|columnKind\|case 'status'" src/web/tectika-board/src/components/board/ --include="*.tsx" -l
```

Open the file found and locate where columns are rendered (likely a switch/case or if-chain on `column.kind`).

Add a `'result'` case that renders `task.artifactSummary`:

```tsx
case 'result':
  return (
    <td key={col.id} className="px-3 py-2 text-xs text-[var(--muted)] truncate max-w-[220px]" title={task.artifactSummary}>
      {task.artifactSummary ?? '—'}
    </td>
  );
```

Also add `'result'` as a default built-in column option in the column picker/registry (look for where default columns are defined — likely an array of `ColumnDef` objects):

```typescript
{ id: 'result', kind: 'result', title: 'Result', width: 220 },
```

- [ ] **Step 3: Commit**

```
git add src/web/tectika-board/src/lib/types.ts
git add src/web/tectika-board/src/components/board/
git commit -m "feat(board): add Result column showing latest artifact summary"
```

---

## Verification

### End-to-end: Selection flow
1. Create an agent role whose system prompt includes: "You are a hotel search agent. Search the web for hotels and end your response with the ## INTERACTION_REQUIRED section."
2. Create a board, add a task "Find Hotel", assign the agent role
3. Trigger the run via `POST /api/runs/start`
4. Watch SSE stream — expect `interaction_required` event
5. `GET /api/interactions/pending` — expect one item with `type: "Selection"`
6. Open `/interactions` page — expect `InteractionCard` showing hotel option cards
7. Select a hotel, click "Confirm Selection"
8. Verify `POST /api/interactions/{id}/respond` returns 200
9. Verify the pipeline resumes (SSE shows `run_completed`)
10. Verify `task.taskBrief` contains `[Human, ..., Selection]: Selected "..."`

### End-to-end: Question flow
1. Agent role system prompt returns `## INTERACTION_REQUIRED` with `type: "Question"` and `question`, `options`
2. Interactions page shows Question card with radio buttons
3. User clicks an option → pipeline resumes

### Legacy approval compatibility
1. Create a pipeline with `ApprovalGate` step
2. `/interactions` page shows the legacy approval card (no selection UI, just approve/reject)
3. Approve → pipeline resumes as before

### Result column
1. After a task completes, open board Table view
2. Add the "Result" column via column picker
3. Verify the column shows the `artifactSummary` text extracted from `## Artifact Summary` JSON
