# Chat tab improvements — design

**Date:** 2026-06-15
**Surface:** `src/web/tectika-board` (AgentBoard frontend)

Two improvements to the task workspace:

1. When no agent owns a task, the **Chat** tab shows an "assign an agent" state with an inline agent picker — not a usable chat interface.
2. A **per-task Run button** that triggers the agent for a single task (today the only automatic trigger is the board-wide "Run Board").

## Context

- The Chat tab is rendered by `ChatTab` in
  [`ItemPanel.tsx`](../../../src/web/tectika-board/src/components/workspace/ItemPanel.tsx) (around line 393).
- A task's owner is `task.assignee` — `{ type: 'Agent' | 'Human', id: string }` (`src/lib/types.ts`).
- Assignment already happens through the DetailsTab `Owner` dropdown via
  `updateTask(task.id, { assignee: { type, id } })`.
- `runBoard()` in `src/lib/board-context.tsx` filters tasks (Agent-owned, `Backlog`, no unmet
  `Dependency` edge) and calls `api.runs.start(boardId, taskId)` for each, setting a board-level
  `runPhase` that the Run Board button reflects.
- Run state is observable through `runsById[task.workflowRunId]?.status`. Terminal statuses:
  `AwaitingInteraction`, `Completed`, `Failed`, `Cancelled`.
- Card-style board views are `CardsView` and `KanbanView` (under `src/components/board/`); both render
  task cards with an existing `group` hover class. Other views (table, calendar, timeline, chart,
  canvas) are not card-based.

## Feature 1 — "Assign an agent" empty state

In `ChatTab`, when `task.assignee.type !== 'Agent'`, render an empty state **instead of** the chat
status bar, message list, and composer:

- Robot icon (`Icon.robot`) centered.
- Heading: **"No agent assigned"**.
- Subtext: *"Assign an agent to this task to start a conversation and run it."*
- An inline agent picker: a `<select>` listing every `people` entry with `kind === 'Agent'`, preceded
  by a disabled "Choose an agent…" placeholder option. Choosing one calls
  `updateTask(task.id, { assignee: { type: 'Agent', id } })`.

Selecting an agent flips `task.assignee.type` to `'Agent'`, so the component re-renders into the
normal chat interface — no extra state or callbacks. The agent-option list is the same set used by
the DetailsTab `Owner` dropdown; extract it into a small shared helper/component so it isn't
duplicated.

**Trigger rule:** the empty state shows whenever the owner is not an Agent (human-owned or
unresolved). The chat interface shows only when `assignee.type === 'Agent'`.

### Components / data flow

- `ChatTab` reads `task.assignee` and `people` (via `useBoard()` / props already in scope).
- No backend changes; assignment reuses the existing `updateTask` → `PUT /api/boards/{boardId}/tasks/{taskId}` path.

## Feature 2 — Per-task Run button

### Shared logic: `runTask(taskId)` in `board-context.tsx`

Add next to `runBoard` and expose it on the context value:

1. Look up the task. No-op (return) unless `task.assignee?.type === 'Agent'`.
2. No-op if the task is already running (see derived state below).
3. Call `api.runs.start(boardId, taskId)`; on success store the returned `runId` on the task locally
   via `updateTask(taskId, { workflowRunId: runId })` so the existing run-polling / SSE effect picks
   it up. On failure, `toast('Could not start run', 'error')`.

Per-task running state is **derived**, not stored in `runPhase`:

```
isTaskRunning(task) =
  !!task.workflowRunId &&
  runsById[task.workflowRunId] &&
  !TERMINAL_STATUSES.includes(runsById[task.workflowRunId].status)
```

This keeps the single-task button independent of the board-level `runPhase`, so a per-task run never
hijacks the Run Board indicator and vice versa. A short-lived optimistic flag (e.g. a `startingIds`
set, or relying on the `await api.runs.start` resolving before re-enabling) covers the gap between
clicking and the run appearing in `runsById`.

### Reusable component: `RunTaskButton`

A small component taking a `task` (and reading `runTask` / run state from `useBoard()`), rendering
per the Smart rules:

| Condition | Rendering |
|---|---|
| Agent owner, idle | `▶ Run` — enabled, calls `runTask(task.id)` |
| Already running | `● Running` — disabled, spinner |
| Human owner | not rendered (hidden) |
| Unmet `Dependency` edge | `▶ Run` enabled, with a tooltip warning that upstream tasks aren't done |

"Unmet dependency" = some edge with `targetTaskId === task.id` and `kind === 'Dependency'` (same
predicate `runBoard` uses). It only warns; it does not block, so a user can force a single task.

### Placement

1. **Item-panel header** — in the header row of `PanelInner` (`ItemPanel.tsx`), next to the
   status/priority pills and assignee avatar.
2. **Board cards** — a hover-revealed run affordance on `CardsView` and `KanbanView` cards, using the
   cards' existing `group` / `group-hover` hover pattern.

**Scope boundary:** the card button is added only to `CardsView` and `KanbanView`. Table, calendar,
timeline, chart, and canvas views are out of scope for this change; the item-panel header button
remains the universal entry point for those.

## Error handling

- `runTask` wraps `api.runs.start` in try/catch and surfaces failures with the existing `toast`
  helper. Clicking while already running is a no-op.
- Feature 1's picker change goes through `updateTask`, which already handles optimistic update +
  toast-on-failure.

## Testing

- Manual visual QA (per the "Running AgentBoard for visual QA" workflow):
  - Chat tab on a human-owned task shows the assign-an-agent state; selecting an agent reveals the
    chat interface.
  - Chat tab on an agent-owned task is unchanged.
  - Run button: visible/enabled for idle agent-owned tasks; hidden for human-owned; shows Running and
    is disabled while a run is in flight; appears on Cards/Kanban cards on hover and in the panel
    header.
- If component-level tests exist in the package, add coverage for the `ChatTab` owner-branch and the
  `RunTaskButton` state matrix.

## Out of scope

- New backend endpoints (reuses `api.runs.start`, `api.tasks.update`).
- Run button on non-card board views.
- Changes to how runs stream or to the board-level Run Board flow.
