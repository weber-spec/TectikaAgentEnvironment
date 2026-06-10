# Human Interaction System — Design Spec

**Date:** 2026-06-09  
**Status:** Approved for implementation

---

## Context

The system currently supports `ApprovalGate` pipeline steps — binary approve/reject checkpoints defined statically in the pipeline. As the system expands to internet-search use cases (e.g., vacation planning), two gaps appear:

1. **Agents need to return structured options** for the user to choose from (hotel results, flights, car rentals), not just free-text artifacts.
2. **Agents need to ask questions dynamically** during a run — not just at pre-defined pipeline checkpoints.

This spec unifies these patterns into a single **Human Interaction** model that replaces and supersedes the `Approval` model, while remaining fully backward-compatible with existing approval gates.

---

## Scope

In scope:
- New `HumanInteraction` data model (3 types: Approval, Selection, Question)
- Agent signaling mechanism via `## INTERACTION_REQUIRED` response section
- Updated orchestrator pause/resume flow
- Updated frontend interaction card UI
- Result display: per-task artifact card + board-level Result column
- Summary Agent pattern (documentation, no new infrastructure)

Out of scope:
- Router/supervisor agent (Phase 2)
- Multi-select interactions (always single-selection)
- Notifications (Teams/email) — Phase 2
- Bicep/deployment changes

---

## Interaction Types

### Approval (existing, enhanced)
Binary yes/no gate. Currently pre-defined in pipeline. No change to behavior; UI gets minor enhancement to show more context.

### Selection (new)
Agent presents N options; user picks exactly one. Used for search results: hotels, flights, car rentals.

### Question (new)
Agent asks a free-text or multiple-choice question. User answers before the pipeline continues.

---

## Data Model: `HumanInteraction`

New Cosmos container `humanInteractions` (partition key: `/taskId`).  
The existing `approvals` container is kept for backward compatibility but receives no new writes.

```csharp
public class HumanInteraction
{
    public string Id { get; set; }
    public string TenantId { get; set; }
    public string RunId { get; set; }
    public string TaskId { get; set; }
    public int StepIndex { get; set; }
    public InteractionType Type { get; set; }      // Approval | Selection | Question
    public InteractionStatus Status { get; set; }  // Pending | Responded | Expired

    // Common
    public string ActionDescription { get; set; }  // shown as card title
    public List<string> RequestedFrom { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }  // default +48h
    public string RespondedBy { get; set; }?
    public DateTimeOffset? RespondedAt { get; set; }

    // Selection fields
    public List<SearchResultItem>? Items { get; set; }
    public int? SelectedIndex { get; set; }

    // Question fields
    public string? Question { get; set; }
    public List<string>? QuestionOptions { get; set; }  // null = free text
    public string? Answer { get; set; }

    // Approval fields
    public string? Notes { get; set; }             // approval notes / rejection reason
    public bool? Approved { get; set; }
    public string? IdentityToBeUsed { get; set; }  // legacy from Approval
}

public class SearchResultItem
{
    public string Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Price { get; set; }
    public List<string>? Details { get; set; }
    public string? Link { get; set; }
    public string? ImageUrl { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public enum InteractionType { Approval, Selection, Question }
public enum InteractionStatus { Pending, Responded, Expired }
```

### `RunStatus` addition
Add `AwaitingInteraction` to the existing `RunStatus` enum (alongside `PausedApproval`).  
Task status mirrors this: when a run is `AwaitingInteraction`, the task shows the same.
```

---

## Agent Signaling: `## INTERACTION_REQUIRED`

Foundry agents communicate interaction needs via a structured section in their response — identical in mechanism to the existing `## Brief Update` and `## Artifact Summary` sections already parsed by `InvokeAgentActivity`.

### Example — Selection (hotel search)
```
## INTERACTION_REQUIRED
{
  "type": "Selection",
  "actionDescription": "בחר מלון לחופשה בפורטוגל",
  "items": [
    {
      "title": "Hotel Bairro Alto",
      "subtitle": "Lisbon, Portugal · ⭐ 4.7",
      "price": "$148/night",
      "details": ["Free breakfast", "Pool", "Central location"],
      "link": "https://...",
      "metadata": { "checkIn": "2026-07-10", "checkOut": "2026-07-15" }
    },
    {
      "title": "Hotel Lisboa Plaza",
      "subtitle": "Central Lisbon · ⭐ 4.4",
      "price": "$112/night",
      "details": ["City view", "Bar"],
      "link": "https://..."
    }
  ]
}
```

### Example — Question
```
## INTERACTION_REQUIRED
{
  "type": "Question",
  "actionDescription": "אישור תקציב לפני חיפוש",
  "question": "מהו התקציב המקסימלי ללילה במלון?",
  "options": ["עד $100", "$100–200", "מעל $200"]
}
```

### Example — Approval (agent-triggered, not pipeline step)
```
## INTERACTION_REQUIRED
{
  "type": "Approval",
  "actionDescription": "הסוכן מבקש אישור לבצע הזמנה",
  "notes": "נמצאה עסקה מוגבלת בזמן. להמשיך?"
}
```

---

## Backend Changes

### `InvokeAgentActivity.cs`
After parsing `## Brief Update` and `## Artifact Summary`, add parsing for `## INTERACTION_REQUIRED`:
1. Extract and parse JSON block
2. Strip the section from `artifact.Content` before saving
3. Set `result.PendingInteraction = new PendingInteractionRequest { ... }` on the return value

### `TaskPipelineOrchestrator.cs`
After each `AgentExecution` step, check if `stepResult.PendingInteraction != null`:
1. Call new `WriteInteractionActivity`
2. `WaitForExternalEvent($"interaction-{step}", TimeSpan.FromHours(48))`
3. On resume: append user's response to `task.TaskBrief` via `AppendTaskBriefActivity`
4. Continue pipeline

No new `StepType` needed — interaction is embedded within the agent step.

### `WriteInteractionActivity.cs` (new)
Mirror of `WriteApprovalActivity`:
1. Save `HumanInteraction` to Cosmos
2. Publish `interaction_required` event via Service Bus → SSE → frontend

### `InteractionsController.cs` (new)
- `GET /api/interactions/pending` — all pending for current user
- `POST /api/interactions/{id}/respond` — submit response (answer/selectedIndex/approved+notes)
  - Validates status == Pending
  - Saves response fields + respondedBy + respondedAt
  - Raises Durable Functions external event: `interaction-{stepIndex}` with response payload
  - Updates `task.TaskBrief` with formatted response line

### Task Brief Format for Responses
```
[Human, {interactionId[..6]}, Selection]: Selected "Hotel Bairro Alto" — $148/night, Lisbon center, check-in July 10
[Human, {interactionId[..6]}, Question]: "$100–200 per night"
[Human, {interactionId[..6]}, Approval]: Approved — "נראה טוב, להמשיך"
```

---

## Frontend Changes

### `/interactions` page (replaces `/approvals`)
The existing `/approvals` page is renamed to `/interactions`. It fetches from **both** `GET /api/approvals/pending` (legacy) and `GET /api/interactions/pending` (new) and merges the lists — ensuring existing approval gates continue to appear. The existing `ApprovalCard` is kept for items from the legacy endpoint. New items from `/api/interactions/pending` render as `InteractionCard` based on `interaction.type`:

**Approval card** — existing layout, no functional change. Add `notes` textarea.

**Selection card:**
- Title: `actionDescription`
- Grid of `SearchResultItem` cards: title + subtitle + price + details list + external link
- Radio button per card (single-select enforced)
- Disabled "Confirm Selection" button until a radio is selected
- On submit: POST with `{ selectedIndex: N }`

**Question card:**
- Title: `actionDescription`
- Question text displayed prominently
- If `questionOptions` is non-null: radio buttons for each option
- If `questionOptions` is null: free-text `<textarea>`
- Submit button: "Send Answer"
- On submit: POST with `{ answer: "..." }`

### Board Table View — Result Column
Add built-in column `result` to the table view column registry.  
Value: `task.latestArtifact?.summary` (already populated by agent's `## Artifact Summary` section).  
Display: single-line text, truncated at ~80 chars, no interaction needed.

---

## Result Display

### Per-task (Selection completed)
When `task.status == Completed` and `latestArtifact.contentType == SearchResults`:  
Show the **selected item** as a prominent card in the Details tab — not the full list.  
Card shows: title, subtitle, price, details, external booking link.

### Board-level (Result column)
The `result` column in Table view shows `artifact.summary` for each completed task.  
Example row: `Find Hotel | Completed | Hotel Bairro Alto, $148/night, check-in July 10`

### Summary Agent Pattern
For a full itinerary, users add a final task (e.g., "Trip Summary") with `upstreamTaskIds` pointing to all search tasks. The agent receives all selected artifacts via the existing `ContextManager.BuildContextAsync()` / `inputContext` mechanism and produces a compiled Markdown document. No new infrastructure needed.

---

## New SSE Event

```json
{ "type": "interaction_required", "runId": "...", "taskId": "...", "step": 2,
  "interactionType": "Selection", "interactionId": "..." }
```

Frontend subscribes to this and shows a badge/notification on the Interactions page.

---

## Verification

1. **Selection flow:** Create a task with an agent role whose system prompt includes search instructions. Run task → agent returns `## INTERACTION_REQUIRED` with type=Selection → task status shows `AwaitingInteraction` → Interactions page shows card with option cards → user selects → task moves to Completed → dependent task starts and receives selected item in context via TaskBrief
2. **Question flow:** Same as above but type=Question — user sees text input or radio buttons, submits answer, pipeline resumes
3. **Approval flow (existing):** Existing pipeline ApprovalGate continues to work unchanged via old `approvals` container and controller
4. **Result column:** Board table view shows artifact summary in Result column for completed search tasks
5. **Summary Agent:** Create board with Hotel+Flights+Car tasks + Summary task; verify Summary agent receives all 3 upstream artifacts and produces compiled itinerary
