# Human-in-the-loop for steerable runs (unified) — Design

**Date:** 2026-06-16
**Status:** Approved for planning (user waived spec-review gate)
**Branch:** feat/chat-tab-improvements

## Problem

When a steerable (chat) agent run calls a control tool — `request_approval`, `request_human_input`, or `request_revision` — the run pauses (`RunStatus.AwaitingInteraction`) and waits for the user. Today that pause produces **only** a `RunEvent` trace entry + a status change. It creates **no persisted request record**, so:

- Nothing appears in the **Approvals** tab or in **notifications**.
- The only way to notice/answer it is to be sitting in that task's chat.

With "run board" launching many tasks at once, approvals can pile up across tasks with no unified place to see or manage them. The user needs the request to surface in the chat **and** in the Approvals tab **and** in notifications, and to be answerable from any of those surfaces.

## Goals

- A paused steerable run produces a **persisted, queryable request** that appears in chat, the Approvals tab, and notifications.
- Responding from **any** surface resolves the request **everywhere** and resumes the run.
- Maximum reuse of the existing `HumanInteraction` system (records, pending lists, `InteractionCard`, notification mapping) — no parallel system.
- Plus a small chat tweak: **Enter sends, Shift+Enter newline**.

## Non-goals

- No changes to the old `TaskPipelineOrchestrator` approval/interaction behavior.
- No live auto-refresh of the Approvals *page* beyond what exists (the notification bell is already SSE-live; the Approvals page fetches on mount — unchanged).
- No new `Approval`-model records; steerable requests use `HumanInteraction` exclusively (it carries `boardId` and the richer variants).

## Background (verified in code)

- The steerable orchestrator resumes by raising the `user_message` external event on its instance ([SteerableAgentOrchestrator.cs:67,92](src/workflows/Orchestrators/SteerableAgentOrchestrator.cs#L67); [HttpTrigger.cs:85](src/workflows/Triggers/HttpTrigger.cs#L85)). `WorkflowRun.DurableFunctionInstanceId` holds the instance.
- The Approvals tab already queries `api.approvals.pending()` **and** `api.interactions.pending()` (tenant-scoped, global across tasks) and renders `HumanInteraction`s via `InteractionCard` ([approvals/page.tsx](src/web/tectika-board/src/app/approvals/page.tsx)).
- `interaction_required` AgentEvents already flow to notifications: `ServiceBusListenerService:71` → `NotificationMapper.Map` → `NotificationRepository.SaveAsync` + `NotificationConnectionManager.BroadcastAsync` (global SSE) → bell ([useNotifications.ts](src/web/tectika-board/src/lib/useNotifications.ts)).
- The old pipeline creates these records via `WriteInteractionActivity` and resumes via the `interaction-{stepIndex}` event in `InteractionsController.respond`. `HumanInteraction` has **no field** indicating which orchestration/event resumes it — both controllers hardcode the old-pipeline event names. This is the gap to close for steerable.

## Design

### A. Add one discriminator field to `HumanInteraction`

`src/core/TectikaAgents.Core/Models/HumanInteraction.cs` — add:

```csharp
[JsonPropertyName("origin")]
public InteractionOrigin Origin { get; set; } = InteractionOrigin.Pipeline;
```
and an enum `InteractionOrigin { Pipeline, Steerable }` (with `JsonStringEnumConverter`). Default `Pipeline` keeps every existing record/behavior unchanged.

### B. Create the record when a steerable run pauses (workflows)

In `RunAgentRoundActivity.Run`, after the existing `RunEventFactory` loop, when `outcome.Kind == RoundKind.AwaitUser && outcome.Control is not null`, create a `HumanInteraction` and publish the existing `interaction_required` event (which drives the Approvals tab + notifications):

- **Stable id** `"{runId}-r{round}-interaction"` (idempotent across activity retries — upsert).
- Fields: `RunId=input.RunId`, `TaskId`, `BoardId`, `TenantId`, `StepIndex=input.Round`, `Origin=Steerable`, `Status=Pending`, `RequestedAt=now`, `ExpiresAt=now+48h`, `RequestedFrom=[task.HumanAuditorId]` if set else `[]`.
- **Control → type mapping** (from `outcome.Control.Kind` / `.Text` / `.Options`):

  | `PendingControlKind` | `InteractionType` | Fields set |
  |---|---|---|
  | `Approval` | `Approval` | `ActionDescription = Control.Text` |
  | `HumanInput` (Options non-empty) | `Question` | `ActionDescription`/`Question = Control.Text`, `QuestionOptions = Control.Options` |
  | `HumanInput` (no Options) | `Question` | `ActionDescription`/`Question = Control.Text` |
  | `Revision` | `Question` | `ActionDescription`/`Question = Control.Text` |

- Persist via an idempotent upsert (`WorkflowCosmosService` — add `UpsertInteractionAsync` if `CreateInteractionAsync` is create-only), then `WorkflowEventPublisher.PublishInteractionRequiredAsync(...)`.

This stays inside the activity (replay-safe); the orchestrator is untouched.

### C. Resume a steerable interaction on respond (API)

`InteractionsController.respond` ([InteractionsController.cs:46](src/api/TectikaAgents.Api/Controllers/InteractionsController.cs#L46)) branches on `interaction.Origin`:

- **`Pipeline`** → unchanged (`interaction-{stepIndex}` path).
- **`Steerable`** →
  1. Mark responded as today (status, respondedBy/At, and the type field: `Approved`+`Notes` / `Answer` / `SelectedIndex`).
  2. Render the response to **natural-language text**:
     - Approval: `Approved.` or `Rejected.`; if `Notes` present, append ` {Notes}`.
     - Question (free-text or option): the `Answer`.
     - (Selection fallback, not produced by steerable: the selected item's title.)
  3. Load `run = GetRunAsync(interaction.TaskId, interaction.RunId)`; raise the **`user_message`** event on `run.DurableFunctionInstanceId` with that text (new `RaiseUserMessageEventAsync`, mirroring the existing durable-management raiseEvent calls).

### D. Keep surfaces consistent when the user free-types a reply (API)

`ChatService.SendAsync`, when it injects into an active run whose status is `AwaitingInteraction` ([ChatService.cs:55](src/api/TectikaAgents.Api/Services/ChatService.cs#L55)), also resolves the task's pending steerable interaction: find the `Pending` `HumanInteraction` for `taskId` with `Origin=Steerable`, set `Status=Responded`, `Answer=text`, `RespondedAt=now`, and persist. So the request leaves the chat card, the Approvals tab, and the notification list regardless of *how* it was answered.

### E. Surfaces (frontend)

- **Chat** (`ItemPanel.tsx` `AgentChat`): when `task.status === 'AwaitingInteraction'`, fetch `api.interactions.pending()` and find the entry with `taskId === task.id`; render the existing **`InteractionCard`** inline where the live edge sits (the live edge is already hidden when status isn't `InProgress`). `onResponded` → clear local pending + `refreshTask(task.id)`; the run resumes (status → `InProgress`) and the live edge returns. Re-fetch when `task.status` changes.
- **Approvals tab + notifications:** no changes — they already render `HumanInteraction`s and map `interaction_required` to notifications. This is the unified cross-task surface the run-board case needs.

### F. Enter-to-send (frontend)

In the `AgentChat` textarea `onKeyDown`:
- The slash-command palette keeps priority: when the `/` menu is open, `ArrowUp/Down/Enter/Esc` drive the menu (unchanged).
- Otherwise: **`Enter` (no Shift) → send**; **`Shift+Enter` → newline** (don't `preventDefault`). The current `⌘/Ctrl+Enter` requirement is dropped (plain Enter now sends).

## Data flow

```
steerable round → AwaitUser
  → RunAgentRoundActivity: create HumanInteraction(Origin=Steerable) + publish interaction_required
      → Approvals tab (api.interactions.pending) shows it
      → ServiceBusListener → NotificationMapper → bell notification
      → chat (status=AwaitingInteraction) fetches pending → InteractionCard
  → user responds (chat card OR Approvals tab OR after a notification):
      api.interactions.respond → Origin=Steerable → render text → raise user_message(instanceId) → run resumes
      (or user free-types in chat → SendAsync injects user_message + marks the interaction Responded)
```

## Files touched

**Core**
- `src/core/.../Models/HumanInteraction.cs` — `InteractionOrigin` enum + `Origin` field.

**Workflows**
- `src/workflows/Activities/RunAgentRoundActivity.cs` — create the interaction on `AwaitUser` + publish `interaction_required`.
- `src/workflows/Services/WorkflowCosmosService.cs` — `UpsertInteractionAsync` (idempotent by stable id) if needed.

**API**
- `src/api/.../Controllers/InteractionsController.cs` — `Origin == Steerable` branch + `RaiseUserMessageEventAsync`.
- `src/api/.../Services/ChatService.cs` — resolve the pending steerable interaction on inject.
- `src/api/.../Services/ICosmosDbService.cs` (+ impls) — `UpdateInteractionAsync`/pending lookup if not already exposed where needed.

**Web**
- `src/web/.../components/workspace/ItemPanel.tsx` — render `InteractionCard` in chat for the task's pending interaction; Enter-to-send.
- `src/web/.../lib/types.ts` — add `origin` to the `HumanInteraction` TS type.

## Testing

- **Backend:** `dotnet build`; unit tests for the control→type mapping + the steerable text rendering (Approval/Question), and that `Pipeline` records still take the old path.
- **Frontend:** `npx tsc --noEmit`, `npx eslint`, `npm run build -- --webpack`.
- **Live smoke (deployed):** drive an agent to call `request_approval`; confirm (1) a notification fires, (2) the Approvals tab lists it, (3) the chat shows the `InteractionCard`, (4) Approve from the chat resumes the run and the entry disappears from all surfaces, (5) repeat answering from the Approvals tab, (6) repeat by free-typing a chat reply — the Approvals entry clears.
- **Deploy:** api + workflows + web.

## Edge cases

- **Activity retry** → stable interaction id + upsert ⇒ no duplicate.
- **Answered two ways at once** (card + free type) → both mark `Responded` (idempotent) and both raise `user_message`; the orchestrator consumes the first and re-arms — a second `user_message` is drained as steering on the next round (benign).
- **`/clear` or `/stop` while awaiting** → out of scope to auto-expire the record; it remains `Pending` until answered or its 48h expiry. Noted, not handled now.
- **Run already resumed before the card loads** (status no longer `AwaitingInteraction`) → chat doesn't render the card; pending fetch returns nothing.
- **No `HumanAuditorId`** → `RequestedFrom = []`; visibility is tenant-wide, so it still shows.
