# Board Settings — Workspace Control, Reset & Clone — Design

**Status:** approved (design); ready for implementation plan
**Date:** 2026-06-28
**Branch:** `feat/board-settings-management`

## Problem

The board's settings live in a small kebab/gear `Menu` ([`BoardView.tsx:112-151`](../../../src/web/tectika-board/src/components/board/BoardView.tsx)) exposing only Rename / Edit description / Connect GitHub / Delete. As the product grows we need richer, riskier board-management actions that don't fit a dropdown:

1. **Reset a board** — wipe all produced work (data, artifacts, files; destroy the ACI and start fresh), keeping only the board's items (all returned to Backlog) and its plan structure. Dangerous; lives at the end of settings.
2. **Clone a board** — duplicate a board with or without its data.
3. **View & control the board's ACI** — see whether the workspace is running, and start / restart / terminate it.

These need a real home, so the small dropdown becomes a dedicated **Board Settings window**.

## Decisions (locked with user)

- **Reset scope = "keep plan, wipe work."** Survivors: items (→ Backlog), dependency/QA edges, agent roles, views/columns. Cleared: artifacts, run history, chat/context, usage, workspace files.
- **"Clear repo" toggle = disconnect → standalone.** Reset never modifies the external GitHub remote. When ON, the board's GitHub connection is removed and the board becomes a standalone (no-repo) board with an empty workspace. When OFF, the remote is left connected and the fresh ACI simply re-clones it.
- **Clone "with data" = deliverables + files, standalone.** Copies items (keeping statuses), edges, roles, views, each task's latest artifact, and a snapshot of the workspace files. The clone is always standalone.
- **New global invariant: a GitHub repo may be connected to at most one board** (per tenant). Enforced at connect time.
- **Settings UI = tabbed window** (General / Repository / Workspace / Danger Zone).
- Reset **auto-cancels** active runs rather than refusing.
- Reset and Clone run **synchronously** (with a spinner) for now.

## Cross-cutting rule — one repo ⇄ one board

A new tenant-wide invariant, enforced in `ConnectGitHub`:

- Connecting a repo URL that is already connected to **another** board in the same tenant is rejected with **409 Conflict** and a clear message.
- Re-connecting the **same** board to the **same** repo is idempotent (allowed).
- Normalization: compare on a canonical form of the repo (lowercased `owner/repo`, derived from the stored `GitHubRepoConnection.Owner`/`Repo`, tolerant of `.git` suffix and trailing slash) so `https://github.com/o/r`, `…/o/r.git`, and `…/o/r/` collide.
- This makes Clone (always standalone) and reset's "disconnect" path coherent.
- **Not retroactive**: boards already sharing a repo are left as-is; the rule only gates new connections.

---

## Component 1 — Board Settings window

Replace the gear `Menu` with a `BoardSettingsModal` built on the existing `Modal` ([`overlays.tsx:114-147`](../../../src/web/tectika-board/src/components/ui/overlays.tsx)). Left-nav tab list; right-side content panel. The gear button opens this modal (no more dropdown).

Tabs:

- **General** — Board name + description (the existing edit form, moved inline), and a **Clone board** button (→ clone dialog).
- **Repository** — GitHub connect/disconnect. Reuses the `GitHubConnectModal` body inline; surfaces the one-repo-per-board 409 as an inline error.
- **Workspace** — ACI status + controls (Component 2).
- **Danger Zone** (rendered last; owner-only) — **Reset board** and **Delete board** (moved here). Both behind a type-the-board-name confirmation.

Notes:
- Views/columns/custom cells are client-side localStorage keyed by board id; reset leaves them untouched, so "keep views" is automatic.
- Non-owners do not see the Danger Zone tab (matches today's owner-gated Delete).
- Accessibility: the tab list is keyboard-navigable; the confirm inputs have labels; destructive buttons keep the existing danger styling (`#e2445c`) and disabled/loading states.

---

## Component 2 — Workspace (ACI) control

One ACI per board, named `tws-{boardId[0..8]}` ([`WorkspaceService.cs`](../../../src/workflows/Services/WorkspaceService.cs)), provisioned lazily on first run and auto-destroyed after 10 min idle ([`IdleWorkspaceCleanupTrigger.cs`](../../../src/workflows/Triggers/IdleWorkspaceCleanupTrigger.cs)). Today there is no status/start/stop surface.

### API (on `BoardsController`, backed by `IWorkspaceService` already injected there)

| Method & route | Behaviour |
|---|---|
| `GET /api/boards/{id}/workspace` | Status DTO (below). |
| `POST /api/boards/{id}/workspace` | **Start** — provision now via `EnsureBoardContainerAsync`, plus no-repo snapshot restore, so the board main line is warm without a run. Idempotent if already Ready. |
| `POST /api/boards/{id}/workspace/restart` | Terminate then provision. |
| `DELETE /api/boards/{id}/workspace` | **Terminate** now — `DestroyBoardContainerAsync`, set `WorkspaceStatus = None`, clear container/endpoint. **Blocked (409)** if the board has active runs. |

### Status DTO

```
{
  status: "None" | "Provisioning" | "Ready",   // Board.WorkspaceStatus (Cosmos)
  azureState: "Running" | "Stopped" | "Failed" | "NotFound" | "Unknown",  // live ARM instance-view
  containerName, endpoint,
  lastUsedAt, idleShutdownAt,                   // lastUsedAt + 10 min
  hasActiveRuns,
  image
}
```

`azureState` comes from a new `IWorkspaceService.GetBoardContainerStatusAsync(boardId)` querying `Azure.ResourceManager.ContainerInstance` (`group.Data.InstanceView?.State`), returning `NotFound` when the group is absent.

### Service additions (`WorkspaceService`)

- `GetBoardContainerStatusAsync(board)` — read ARM instance-view state.
- `EnsureWorkspaceForBoardAsync(board)` — board-level provision (+ no-repo restore) without a run worktree, for the Start button. Reuses the existing provisioning + restore code paths.
- `RestartBoardContainerAsync(board)` — destroy + ensure.

### UI (Workspace tab)

Polls `GET …/workspace` while open. Shows: status badge (Not provisioned / Provisioning / Running / Stopped / Failed), container name, endpoint, last-used, idle-shutdown countdown, whether a run is active. Buttons: **Start** (when None/Stopped), **Restart** (when Ready), **Terminate** (when Ready/Provisioning, disabled with explanation if a run is active).

---

## Component 3 — Reset board

`POST /api/boards/{id}/reset` with body `{ clearRepo: boolean }`. Owner-only. Returns a summary `{ tasksReset, artifactsDeleted, runsDeleted, workspaceTerminated, repoDisconnected }`.

Implemented by a new `BoardMaintenanceService`. Steps (best-effort, idempotent, ordered so a retry converges):

1. **Cancel active runs** — for tasks with status in {InProgress, AwaitingInteraction, Blocked} or a non-terminal `WorkflowRun`, terminate the durable orchestration (reuse the existing task-stop path).
2. **Delete produced data**, per task (partition `/taskId` unless noted):
   - all `artifacts`
   - all `workflowRuns` (and, per run, `humanInteractions` and `pendingMessages`, partition `/runId`)
   - all `runEvents`
   - all `usageEvents`; drop the task's `usageRollups` and reset the board rollup
3. **Reset each task → Backlog** and clear work fields: `workflowRunId`, `currentArtifactId`, `taskBrief`, `artifactSummary`, `foundryThreadId`, `pendingOutputs`, `humanAskCount`, fresh `usageSessionId`, `chatClearedAt`. Reset each edge's `currentIterations` to 0.
4. **Workspace** — terminate the ACI (`DestroyBoardContainerAsync`), set `WorkspaceStatus = None`, clear container/endpoint, and delete the `workspace-snapshots/{boardId}.bundle` blob ([`WorkspaceSnapshotStore.cs`](../../../src/workflows/Services/WorkspaceSnapshotStore.cs)). A fresh ACI provisions lazily on the next run.
5. **If `clearRepo` and connected** — disconnect GitHub (clear `Board.github`; delete the PAT secret best-effort). The board becomes standalone; the remote is never touched.
6. **Kept**: board, items, edges, agent roles, views.

### Error handling

- Each delete phase is best-effort and logged; a partial failure still advances the rest and the operation is safely re-runnable.
- ACI destroy uses the existing `WaitUntil.Started` (async) semantics.
- Returns 403 if the caller is not the owner; 404 if the board is missing.

---

## Component 4 — Clone board

`POST /api/boards/{id}/clone` with body `{ name?: string, includeData: boolean }`. Returns the new `Board`. Always standalone (no GitHub, per the global rule). Implemented in `BoardMaintenanceService`.

- **Always copies**: board metadata (`name` defaults to `"Copy of {source.name}"`, description, columns config; owner = caller; no github), items (new ids, old→new map; copy title/description/priority/assignee/dependencies-remapped/canvasPosition/prompt/dueAt/humanAuditorId), edges (remapped), board-scoped agent roles if any (verify scoping during implementation — if roles are tenant-shared, no copy is needed).
- **`includeData = false`**: items → Backlog, no artifacts, empty workspace, edge `currentIterations` → 0.
- **`includeData = true`**: items keep their status; copy each task's **latest** artifact (remap `taskId`/ids; set `currentArtifactId`); copy `artifactSummary`/`taskBrief`; keep edge `currentIterations`. Seed the workspace by copying the source's snapshot blob to `workspace-snapshots/{newBoardId}.bundle`; if the source ACI is currently Ready, bundle it fresh first for an up-to-date seed. The clone restores from its snapshot on first provision (existing no-repo restore path). **Full run-event history is not copied** (kept lean — artifacts carry the deliverables).
- **Client**: on success, copy the source board's localStorage view/column config to the new board's key, then navigate to the new board.

### Error handling & limits

- 404 if the source board is missing.
- Clone of a **connected** source does not deep-copy remote file bytes beyond an existing/fresh snapshot — the clone is standalone and its deliverables travel as artifacts. Documented limitation.
- Synchronous; bounded by board size. Can move to a background job later if needed.

---

## Where new code lands

- **Backend**
  - `BoardMaintenanceService` (reset + clone orchestration), used by `BoardsController`.
  - `IWorkspaceService`: `GetBoardContainerStatusAsync`, `EnsureWorkspaceForBoardAsync`, `RestartBoardContainerAsync`.
  - `CosmosDbService`: bulk delete-by-board helpers (artifacts/runs/runEvents/usage/interactions) and the task-field reset.
  - `BoardsController`: `reset`, `clone`, `workspace` GET/POST/restart/DELETE; uniqueness check in `ConnectGitHub`.
  - `IWorkspaceSnapshotStore`: `DeleteAsync(boardId)` and `CopyAsync(srcBoardId, dstBoardId)` (or download+upload).
- **Frontend**
  - `BoardSettingsModal` + tab panels (General, Repository, Workspace, DangerZone).
  - `api.boards`: `reset`, `clone`, and `workspace.{get, start, restart, terminate}`.
  - `BoardView` wires the gear button to the modal; clone navigation; localStorage config copy on clone.

## Testing (TDD)

- **One-repo-per-board**: connecting an already-connected repo → 409; same board re-connect → ok; normalization collisions (`.git`/trailing slash) detected.
- **Reset (unit, fake stores)**: deletes artifacts/runs/events/usage/interactions for every task; sets every task to Backlog with work fields cleared; resets edge iterations; terminates ACI + deletes snapshot blob; `clearRepo` disconnects vs not; keeps edges/roles; cancels active runs; non-owner → 403; idempotent on re-run.
- **Clone (unit)**: structure copied with remapped ids/edges; `includeData=false` → Backlog + no artifacts + empty workspace; `includeData=true` → statuses kept + latest artifact per task + snapshot blob seeded; always standalone.
- **Workspace control (unit, fake workspace service)**: status DTO maps ARM state; Start provisions (+restore); Terminate blocked with active runs; Restart = destroy+ensure.
- **Frontend**: settings modal tab navigation; reset/clone/terminate confirm flows; workspace status polling renders states.

## Out of scope

- No raw Azure Stop/pause state (Terminate + Start covers it under the ephemeral-ACI + snapshot model).
- Reset/clone are synchronous (can become a background job later).
- Connected-source clone does not deep-copy remote file bytes beyond a snapshot.
- No retroactive enforcement of one-repo-per-board on already-connected boards.
