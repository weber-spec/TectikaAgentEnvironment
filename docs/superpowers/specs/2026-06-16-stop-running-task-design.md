# Stop a running task — design

**Date:** 2026-06-16
**Surfaces:** `src/web/tectika-board` (frontend) + `src/api` (ChatService)

Let the user stop a task's agent run from the same per-task Run button: while a task is
running, the button offers a Stop action, guarded by a confirmation modal.

## Context

- The per-task button is `RunTaskButton`
  ([`RunTaskButton.tsx`](../../../src/web/tectika-board/src/components/board/RunTaskButton.tsx)),
  rendered in the item-panel header and on Cards/Kanban cards.
- It shows a running state when `task.status === 'InProgress'` (the authoritative, live-synced
  signal — see the run-button bugfix). Idle agent tasks show "Run".
- `api.tasks.stop(boardId, taskId)` already exists (POST `/api/boards/{boardId}/tasks/{taskId}/stop`),
  wired into the `/stop` chat command. It calls `ChatService.StopAsync`, which terminates the durable
  orchestration and sets `run.Status = Cancelled`.
- `Modal` is an existing primitive
  ([`overlays.tsx`](../../../src/web/tectika-board/src/components/ui/overlays.tsx)) with
  `title` / `children` / `footer`.
- `refreshTask(id)` (board-context) re-fetches a single task.

### Problem found during design

`StopAsync` ([`ChatService.cs:114`](../../../src/api/TectikaAgents.Api/Services/ChatService.cs#L114))
cancels the run but does **not** update `task.Status`; the `Terminate` durable trigger only kills the
instance. So after a stop the task stays `InProgress`. Because the button keys off `task.status`, the
Stop wouldn't visibly clear. This is also a latent bug in the existing `/stop` command (task stuck
`InProgress`).

## Backend change — reset task status on stop

In `ChatService.StopAsync`, after `run.Status = Cancelled; UpdateRunAsync(...)`, also set
`task.Status = AgentTaskStatus.Backlog` and `UpdateTaskAsync(task)`. `task` is non-null at that point
(the method already returned `false` earlier if the task/run was missing).

Rationale: there is no `Cancelled` task status (`AgentTaskStatus` = Backlog, InProgress,
AwaitingApproval, AwaitingInteraction, Blocked, Review, Done, Failed). `Backlog` means "not running,
re-runnable" — the Run button reappears and Run Board can pick it up again. Fixes the `/stop` command
too.

## Frontend — `stopTask` in board-context

Add `stopTask(taskId)` alongside `runTask`:

1. Call `api.tasks.stop(boardId, taskId)`.
2. On success, optimistically set the task to `Backlog` (`setTasks`), then `refreshTask(taskId)` to
   reconcile with the server.
3. On failure, `toast('Could not stop the run', 'error')`.

Expose `stopTask` on the context value/interface.

## Frontend — RunTaskButton: Running → Stop + confirm modal

When `task.status === 'InProgress'` the button becomes an interactive Stop affordance:

- Track a local `hovered` state (React `onMouseEnter`/`onMouseLeave`, not CSS `group-hover`, to avoid
  clashing with the card's own `group` hover used to reveal the icon).
- **Button mode (header):** default green `● Running` (spinner + label); on hover, red `■ Stop`.
- **Icon mode (cards):** default spinner; on hover, red stop square.
- Clicking while running opens a confirm `Modal`:
  - Title: "Stop this run?"
  - Body: "The agent will cancel its current work."
  - Footer: `Cancel` (secondary) / `Stop run` (danger). `Stop run` calls `stopTask(task.id)` and
    closes the modal.
- Each button instance owns its own modal open-state; `Modal` renders via portal so it overlays
  correctly regardless of which button opened it.
- Stop glyph: a small filled rounded square (`bg-current`) — no new icon needed.

Idle behavior (Run, dependency warning tooltip, hidden for human-owned tasks) is unchanged.

## Error handling

- `stopTask` wraps `api.tasks.stop` in try/catch with the existing `toast` helper.
- Backend `StopAsync` already returns `false` (benign) when there is no active run.

## Testing

- Visual QA (mock DB): on an `InProgress` agent task, hovering the running button shows the red Stop
  affordance (header + card icon); clicking opens the confirm modal; Cancel dismisses; Stop run
  triggers `api.tasks.stop`.
- The end-to-end cancellation needs a live durable instance (absent in the mock DB), so the backend
  status reset is verified by code review and the shared `/stop` path rather than a mock run.
- `tsc --noEmit` clean; no new lint errors.

## Out of scope

- New stop/terminate backend endpoints (reuses `api.tasks.stop` / `StopAsync`).
- Changes to the chat `/stop` command UI (it benefits automatically from the status-reset fix).
- Stop affordance on non-card board views.
