# Phase 0 — Typed Edges (TaskEdge as single source of truth)

**Status:** approved design · **Branch:** `feat/phase0-typed-edges` · **Date:** 2026-06-09

## Context

Tectika's Flow Canvas lets users wire tasks into a graph (dependencies + QA feedback loops). Today the *connection topology* is persisted as `upstreamTaskIds`/`downstreamTaskIds` arrays on each `AgentTask`, but the *typed metadata* is not real data:

- **Edge labels** live only in browser **localStorage** (`tectika:board:{boardId}`, keyed `${source}->${target}`) — per-browser, never synced.
- The **Dependency vs QA-feedback** distinction is a **render-time DFS heuristic** (`feedbackEdgeIds` in `CanvasView.tsx`) — purely visual, nothing authoritative for an execution engine to read.

Phase 0 makes edges a first-class, server-persisted, typed entity so later phases (the graph orchestrator, QA loops) can read authoritative edge semantics. **Decision (locked with the user): a single source of truth** — `TaskEdge` becomes the one representation of an edge (topology + metadata), and the `upstream/downstream` arrays are **removed entirely**, with every consumer (frontend + backend workflow) migrated to read edges.

## Goals / non-goals

- **Goal:** `TaskEdge` model + `taskEdges` Cosmos container + CRUD; canvas and workflow code read/write edges; kind is authoritative (auto-detected on create, user-overridable); labels persisted server-side and synced.
- **Non-goal (deferred to Phase 7):** executing QA loops; editing UI for `condition`/`maxIterations` (fields exist with defaults, no editor yet).
- **Non-goal:** touching the unused `AgentTask.Dependencies` field.

## Data model

New `src/core/TectikaAgents.Core/Models/TaskEdge.cs`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | `"{sourceTaskId}->{targetTaskId}"` — stable, dedupes, matches the UI edge id |
| `tenantId` | string | |
| `boardId` | string | **partition key** |
| `sourceTaskId` | string | |
| `targetTaskId` | string | |
| `kind` | `EdgeKind` | `Dependency \| QaFeedback` (serialized as string) |
| `label` | string? | moved off localStorage |
| `condition` | string? | forward-compat (Phase 7); no UI |
| `maxIterations` | int | default `3`; forward-compat (Phase 7); no UI |
| `createdAt` / `updatedAt` | DateTimeOffset | |

`enum EdgeKind { Dependency, QaFeedback }`.

**Removed:** `AgentTask.UpstreamTaskIds` / `DownstreamTaskIds` (C# model, `src/web/.../lib/types.ts` `AgentTask`, and `TaskPatch`).

## Storage & data access

- New Cosmos container **`taskEdges`** (PK `/boardId`): add to `CosmosDbService` container constants + `EnsureInfrastructureAsync`; mirror in `InMemoryCosmosDbService`; provision in live Cosmos via `az cosmosdb sql container create ... -n taskEdges -p /boardId`.
- `ICosmosDbService` additions: `CreateEdgeAsync(TaskEdge)`, `GetEdgesByBoardAsync(boardId)`, `GetEdgeAsync(boardId, edgeId)`, `UpdateEdgeAsync(TaskEdge)`, `DeleteEdgeAsync(boardId, edgeId)`, `DeleteEdgesForTaskAsync(boardId, taskId)` (cascade). Implement in both Cosmos and in-memory services.

## API — new `EdgesController` (board-scoped)

`src/api/.../Controllers/EdgesController.cs`:

- `GET    /api/boards/{boardId}/edges` → `TaskEdge[]`
- `POST   /api/boards/{boardId}/edges` `{ sourceTaskId, targetTaskId, kind?, label? }` → create
- `PUT    /api/boards/{boardId}/edges/{edgeId}` `{ kind?, label?, condition?, maxIterations? }` → update
- `DELETE /api/boards/{boardId}/edges/{edgeId}` → delete

**Create rules:** reject self-links (`source == target`) and duplicates (existing id). **Auto-detect kind** when not explicitly supplied: load the board's edges, build adjacency over **`Dependency` edges only** (so existing feedback edges don't create phantom cycles), and if `targetTaskId` can already reach `sourceTaskId` (the new edge closes a cycle) → `QaFeedback` (with `maxIterations = 3`), else `Dependency`. This is the server-side authoritative port of today's client reachability check. The endpoint **returns the created `TaskEdge`** (with its authoritative `kind`), which the canvas uses for styling rather than re-deriving locally.

**Removed:** `POST /tasks/{taskId}/connect`, and the `UpstreamTaskIds`/`DownstreamTaskIds` patch handling in `PUT /tasks/{taskId}` + `UpdateTaskRequest`. Task `DELETE` calls `DeleteEdgesForTaskAsync`.

## Backend workflow consumers (the ripple)

`src/workflows/Services/WorkflowCosmosService.cs` + `Activities/InvokeAgentActivity.cs` no longer read `task.UpstreamTaskIds`. New path: `GetUpstreamTaskIdsAsync(boardId, taskId)` = query `taskEdges` for `targetTaskId == taskId && kind == Dependency` → source ids → existing `GetUpstreamArtifactsAsync(sourceIds)`. **QaFeedback edges are control-flow, not data dependencies, so they never contribute upstream artifacts/context** — the kind distinction is load-bearing here. Add a `taskEdges` container reference in `WorkflowCosmosService`.

## Frontend

- `src/web/.../lib/types.ts`: add `TaskEdge` + `EdgeKind`; remove the arrays from `AgentTask` and `TaskPatch`.
- `src/web/.../lib/api.ts`: add `edges` client (`list/create/update/remove`); remove `tasks.connect`.
- `src/web/.../lib/board-context.tsx`: hold `edges: TaskEdge[]` state, fetched on board load and reconciled in the existing **7s poll** (alongside `tasks.list`); remove `edgeLabels` from `BoardConfig`/localStorage; `connectTasks → edges.create`, `disconnectTasks → edges.remove`, `setEdgeLabel → edges.update` (all optimistic).
- `src/web/.../components/board/canvas/CanvasView.tsx`: build display edges from `edges` (not `downstreamTaskIds`); **delete `feedbackEdgeIds` (the DFS)** and style feedback by `edge.kind === 'QaFeedback'`; keep edge id `${source}->${target}`; wire `onConnect/onEdgesDelete/onReconnect`/label edit to the edge API; add a minimal **kind override** (a "feedback loop" checkbox in the existing label popover) → `edges.update`.
- Audit all other consumers of `upstreamTaskIds`/`downstreamTaskIds` (e.g. a dependency column type, any timeline/table view) and repoint them at `edges`.

## Data migration

- **Seed:** `MockDataSeeder` stops setting the arrays; `CosmosDataSeeder` emits `TaskEdge` docs for the demo dependency graph (`task-spec→task-impl→task-review→task-deploy`, etc.) into `taskEdges`.
- **Existing live Cosmos:** a one-time **idempotent backfill** (a new mode alongside `--seed-only`, e.g. `--backfill-edges`) that raw-reads each task's stored `downstreamTaskIds` (Cosmos still retains the field on existing docs even after the model drops it) and creates `TaskEdge` docs with auto-detected kind. Idempotent (skips edges that already exist). Run once after deploy.

## Error handling

- Create on missing source/target task → 404; self-link / duplicate → 409/400. Update/delete missing edge → 404.
- In-memory and Cosmos services return `null`/no-op on missing, matching existing conventions.
- Backfill and seed are best-effort + idempotent; failures logged, startup continues (consistent with the existing `EnsureInfrastructure` try/catch).

## Testing

- **Mock mode** (`MockDatabase:Enabled=true`): edge CRUD; create that closes a cycle → `kind=QaFeedback`; duplicate/self-link rejected; task delete cascades; `GetUpstreamTaskIdsAsync` returns only `Dependency` sources.
- **Frontend:** draw an edge → survives hard refresh with kind + label; second browser sees it within the poll interval; feedback edge styled by stored kind; label edit persists server-side (not localStorage).
- **Backfill:** run against a board whose tasks still have arrays → correct `TaskEdge` docs; re-run is a no-op.
- **Regression:** solution builds clean; the existing Playwright canvas test (expects ≥3 dependency edges) still passes.

## Rollout

1. Backend: model, container, `ICosmosDbService` methods (both impls), `EdgesController`, kind auto-detect, task-delete cascade, remove `/connect` + array patch.
2. Workflow consumers: `GetUpstreamTaskIdsAsync` + `InvokeAgentActivity`.
3. Seed + backfill.
4. Frontend: types/api/context/canvas + consumer audit.
5. Provision `taskEdges` in live Cosmos; run backfill once; verify.
