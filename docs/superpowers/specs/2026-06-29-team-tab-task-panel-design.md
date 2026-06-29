# Team tab — design spec

**Date:** 2026-06-29
**Status:** Approved (brainstorm), pending implementation plan
**Branch:** `worktree-feat+web-team-tab`
**Surfaces:** `src/web/tectika-board` (Next/React/Tailwind) · `src/api/TectikaAgents.Api` (.NET) · `src/core/TectikaAgents.Core` (models) · `infra/` (Bicep)

## 1. Summary

Add a fifth tab — **Team** — to the task `ItemPanel`, alongside Chat, Activity, Details, and CLI bridge. Where the **Chat** tab is a human → agent channel (steering), the **Team** tab is a human ↔ human channel: a candid space for the human team to coordinate, debate approach, and record decisions about a task.

The tab has two zones:

- **Notes** — a small set of durable, editable, typed notes (Decision / Open question / Note). The team's living record for the task.
- **Discussion** — a flat, chronological message feed (PR-comment style) for the back-and-forth.

The agent does **not** see this content by default. Any note can be **explicitly shared to the agent** as a steering message (one action), which reuses the existing chat/steering pipeline.

## 2. Decisions (from brainstorm)

| # | Decision | Choice |
|---|----------|--------|
| D1 | Agent visibility | **Private by default + explicit "Share to agent"** on individual notes. The tab is human-only; sharing is a deliberate, audited action. |
| D2 | Structure | **Two zones**: durable **Notes** (typed, editable) above a flat **Discussion** feed. |
| D3 | Discussion threading | **Flat** feed. No nested replies; use @-mentions to reference people. |
| D4 | v1 extras (all in) | **@-mentions + notifications**, **unread badge** on the tab, **emoji reactions**, **edit/delete own**. |
| D5 | Tab name | **Team**. |
| D6 | Real-time delivery | **4s polling** with a `visibilityState` guard (matches Chat + CLI bridge). SSE push deferred. |

## 3. Out of scope (deliberate, v1)

- **Threading / nested replies** — flat feed is enough for a narrow 420px column.
- **Rich-text editor / file attachments** — markdown only (reuse existing renderer).
- **Typing / presence indicators** — no live presence.
- **Board-card-level unread dots** (unread aggregated across tasks in the board view) — v1 badge lives on the tab inside an open task panel only.
- **SSE live push for comments** — polling for v1; the write path is centralized so SSE can be added later in one place.

Each is a clean follow-on; none is load-bearing for "the team talks and writes notes about a task."

## 4. Data model

### New Cosmos container: `taskComments`

Partition key `/taskId` (mirrors `runEvents`; all of a task's comments live in one partition → a single ordered list query). One document type with a `kind` discriminator covers both zones:

```
TaskComment {
  id: string                      // guid
  taskId: string                  // partition key
  boardId: string                 // for scoping
  tenantId: string                // for auth scoping (matches `tid` claim)
  kind: "note" | "message"
  noteType?: "decision" | "open_question" | "note"   // kind=note only
  authorId: string                // email (preferred_username claim)
  body: string                    // markdown source
  mentions: string[]              // resolved user ids (emails)
  reactions: { [emoji: string]: string[] }            // emoji -> userIds
  createdAt: string               // ISO 8601
  updatedAt?: string              // set on edit
  editedBy?: string
  deletedAt?: string              // soft-delete tombstone
  sharedToAgent?: { at: string, by: string, runId?: string }   // D1 audit
}
```

- This promotes the existing **frontend-only** `Comment` model (`src/web/tectika-board/src/lib/types.ts`) to a real backend entity and extends it (`kind`, `noteType`, `updatedAt`, `editedBy`, `deletedAt`, `sharedToAgent`).
- **Soft-delete:** deleting sets `deletedAt`. A deleted *message* renders as a "message deleted" tombstone (keeps feed continuity); a deleted *note* is removed from the Notes zone. List queries return tombstones for messages; the client decides rendering.
- New container must be registered in **both** `CosmosDbService.ContainerDefinitions` **and** `infra/modules/data.bicep` (see §9).

### Unread tracking — no second container

Store `lastReadAt` per task inside the existing per-user `userSettings` doc (partition `/userId`) as a `teamTabReadAt: { [taskId: string]: string }` map. Avoids provisioning a second container. Marking read is a read-modify-write of the caller's settings doc (single-user concurrency, low risk).

## 5. Backend

### `CommentsController` (new), nested under the task

All endpoints `[Authorize]`; all extract `tid` (tenant) and `preferred_username` (user) from claims; all verify the target task belongs to the caller's tenant before any data access (load task → check `board.tenantId == TenantId`), matching `TasksController`'s pattern.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/api/boards/{boardId}/tasks/{taskId}/comments` | List all comments (notes + messages) for the task, ordered by `createdAt`. Also returns the caller's `lastReadAt`. |
| POST | `/api/boards/{boardId}/tasks/{taskId}/comments` | Create. Body: `{ kind, noteType?, body, mentions[] }`. |
| PUT | `/api/boards/{boardId}/tasks/{taskId}/comments/{id}` | Edit own. Body: `{ body, noteType? }`. **Author-only (403 otherwise).** Sets `updatedAt`/`editedBy`. |
| DELETE | `/api/boards/{boardId}/tasks/{taskId}/comments/{id}` | Soft-delete own. **Author-only.** |
| POST | `/api/boards/{boardId}/tasks/{taskId}/comments/{id}/reactions` | Toggle reaction. Body: `{ emoji }`. Adds/removes caller's id under that emoji. |
| POST | `/api/boards/{boardId}/tasks/{taskId}/comments/{id}/share` | **Share to agent (D1).** Reuses `ChatService.SendAsync(boardId, taskId, tenantId, body, ct)`; stamps `sharedToAgent`. Notes only. |
| POST | `/api/boards/{boardId}/tasks/{taskId}/comments/read` | Mark the Team tab read for the caller (sets `lastReadAt = now`). |

### Share-to-agent bridge (D1)

`/share` builds a steering message from the note body (e.g. prefixed `"[Team note shared by {name}]: {body}"`) and calls the **existing** `ChatService.SendAsync`, which either seeds a new steerable run or injects into the live one. No new agent plumbing. Records `sharedToAgent = { at, by, runId }` on the note for the audit trail; the UI confirms before sending.

### @-mentions + notifications (D4)

On create/edit, parse `@name` tokens against the board roster, store resolved ids in `mentions[]`. For each newly-mentioned user, write a notification into the existing `notifications` container (partition `/tenantId`).

> **VERIFY IN PLANNING:** the exploration did not surface how notifications are *consumed/surfaced* in the UI. Confirm the existing notification shape + delivery path. If notifications aren't surfaced yet, mentions still highlight inline and the unread badge covers awareness; notification writes become best-effort, not a v1 blocker.

## 6. Frontend

### Wiring

- `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`: add `'team'` to the `Tab` union, the tab array, and the conditional render. The tab button shows an unread count badge.
- **New file** `src/web/tectika-board/src/components/workspace/TeamTab.tsx`. `ItemPanel.tsx` is already ~1050 lines and this tab is substantial, so it lives in its own focused file (the existing four tabs stay inline — not refactoring them, out of scope). `TeamTab` fetches its own data like the other tabs, **not** via `board-context`.
- `src/web/tectika-board/src/lib/types.ts`: extend `Comment` → `TaskComment` (add `kind`, `noteType`, `updatedAt`, `editedBy`, `deletedAt`, `sharedToAgent`), keeping existing field names for continuity.
- `src/web/tectika-board/src/lib/api.ts`: add an `api.comments` group: `list`, `create`, `update`, `remove`, `react`, `share`, `markRead` — using the existing `fetchApi<T>` helper.
- `src/web/tectika-board/src/lib/board-context.tsx` + `collaboration.ts`: remove the dead `seedCollaboration` demo `comments[]` path (the tab now uses real data).

### Reuse

`Avatar`, `Button`, `Pill` (`components/ui/primitives.tsx`); the existing `Markdown()` renderer; `relativeTime()` (`lib/format.ts`); the `peopleById` lookup for author avatars/mentions; `CURRENT_USER` (`lib/collaboration.ts`) for author identity until MSAL is wired.

### Layout (approved mockup)

Top → bottom in the 420px left pane: **Notes** zone (header with "+ Add note"; typed cards with a small kind chip, body, "edited by … · time", per-note edit/delete, and a "⤳ Share to agent" action) → divider → **Discussion** zone (label; flat message rows: avatar, name, relative time, markdown body, reaction chips; hover reveals react/edit/delete) → pinned **composer** ("Message the team…", `@` to mention, Send).

### Real-time (D6)

`TeamTab` polls `GET …/comments` every 4s while `document.visibilityState === 'visible'` (the Chat/CLI-bridge pattern). On mount and when the tab becomes active, it also calls `…/comments/read`. Unread badge = count of others' non-deleted comments with `createdAt > lastReadAt`.

## 7. Edge cases & safety

- **Optimistic posting** with rollback on failure; trim empty bodies; disable Send while in-flight.
- **Empty states** for both zones ("No notes yet — capture a decision or open question", "No messages yet").
- **Author-gating** on edit/delete enforced server-side (403) and hidden client-side.
- **Markdown XSS:** content is teammate-authored and rendered to other users. **VERIFY IN PLANNING:** audit the existing `Markdown()` renderer for raw-HTML injection; harden (escape HTML, safe links) if it passes raw HTML through.
- **@-mention parsing:** resolve against board roster; store resolved ids; unknown `@token` renders as plain text.
- **Reactions** are a low-stakes last-write-wins toggle (read-modify-write); a lost concurrent toggle is acceptable.
- **Share to agent:** confirmation dialog before sending; success/failure feedback.
- **Tenant scoping** verified on every endpoint to prevent cross-tenant access.

## 8. Testing

- **Backend** (mock `ICosmosDbService`): create/list; edit & delete author-enforcement (403 for non-author); reaction toggle add/remove; share delegates to `ChatService` and stamps `sharedToAgent`; mark-read updates `userSettings`; cross-tenant request rejected.
- **Frontend** (confirm web app's test setup first): TeamTab renders notes + messages; optimistic post + rollback; @-mention highlight; reaction toggle; author-gated edit/delete visibility; unread-count logic; share-to-agent confirm flow; empty states.
- **Infra:** assert `taskComments` present in both `ContainerDefinitions` and `data.bicep`.

## 9. Deployment

- Add `taskComments` (`/taskId`) to **both** `CosmosDbService.ContainerDefinitions` and `infra/modules/data.bicep`, kept in sync (project idempotency rule: infra must recreate from scratch).
- New Cosmos containers are not reliably auto-created in prod (`EnsureInfrastructureAsync` swallows failures). On deploy, create explicitly via `az cosmosdb sql container create -a <acct> -d tectikaagents -g <rg> -n taskComments --partition-key-path /taskId` or the feature 500s.
- No new container for read markers (folded into `userSettings`) — minimizes infra churn.

## 10. Verify-in-planning checklist

1. Notification consumption/surfacing path (§5) — confirm shape + whether UI surfaces them.
2. `Markdown()` renderer HTML-injection safety (§7) — audit + harden if needed.
3. Web app test framework/setup (§8) — confirm before writing frontend tests.
4. Exact `userSettings` doc shape and update method on `ICosmosDbService` (§4).
5. `ChatService.SendAsync` signature + behavior when no run is active vs. live (§5).
