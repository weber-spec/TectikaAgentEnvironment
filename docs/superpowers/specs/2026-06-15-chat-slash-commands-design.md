# Chat Slash-Commands — Design

**Date:** 2026-06-15
**Status:** Approved (brainstorming) — pending spec review
**Author:** elimeshi + Claude

## Goal

Add slash-command support to the per-task chat: typing `/` opens an escapable, fuzzy-searchable
command palette anchored to the chat input. First-class commands: **`/clear`, `/compact`, `/stop`,
`/retry`, `/help`**. Primary motivation — a long chat can bloat the agent's context; the user wants a
fast way to reset it to a clean slate (and lighter-weight variants).

## Mental model

The agent's per-turn **context** is built from three things: the **Foundry conversation** (the thread,
`AgentTask.FoundryThreadId`), the **TaskBrief** (running notes accumulated each round and fed back),
and the **upstream artifacts** (the task's dependencies). "Clean context" therefore means resetting the
first two; the per-task prompt and upstream artifacts are the task's real inputs and are kept.

Commands are a thin, extensible layer over the existing chat: a frontend **registry** drives the
palette; each command runs client-side, re-uses the existing chat, or calls a small dedicated endpoint.

## Decisions (locked during brainstorming)

| Topic | Decision |
|---|---|
| `/clear` scope | Reset Foundry conversation + clear visible transcript + clear `TaskBrief`. Keep per-task prompt + upstream artifacts. |
| Transcript wipe | **Non-destructive boundary** (`ChatClearedAt` timestamp), NOT a hard delete. UI renders events after the boundary. Reversible; no delete path to build. |
| Command architecture | **Approach A** — declarative frontend registry + thin per-command endpoints (vs. one generic dispatch endpoint, vs. overloading chat). |
| Command set (v1) | `/clear`, `/compact`, `/stop`, `/retry`, `/help`. |
| `/compact` | The only LLM-dependent command; one summarization call → `TaskBrief`, then `/clear`-style reset. Falls back to plain `/clear` on failure. |

## Command semantics

- **`/clear`** — `POST …/clear`: patch task `FoundryThreadId=null`, `TaskBrief=""`, `ChatClearedAt=now`.
  Next run creates a fresh conversation. RunEvents untouched (boundary hides them).
- **`/compact`** — `POST …/compact`: summarize the chat `RunEvent`s since the last boundary via one
  Foundry `/responses` call → write summary to `TaskBrief`, then reset thread + set boundary (keeps the
  gist, drops the bulk). On summarization failure → behave as `/clear` + surface a toast.
- **`/stop`** — `POST …/stop`: load the run, terminate its Durable orchestration via a new workflows
  trigger `POST pipelines/{instanceId}/terminate` (`durableClient.TerminateInstanceAsync`), mark the run
  `Cancelled`. Enabled only while a run is active.
- **`/retry`** — client-only: re-send the **last user message** via the existing `chat` API. Enabled
  only if there is a prior user message and nothing is currently running.
- **`/help`** — client-only: inline list of all commands + descriptions.

## Frontend — the `/` palette

- Trigger: in `ChatTab`, when the draft **starts with `/`**, open a `CommandMenu` as a `Popover`
  anchored **above** the textarea.
- Smart search: text after `/` fuzzy-filters the registry (reuse `fuzzyScore` from
  `src/components/command/CommandPalette.tsx` / `lib/format.ts`).
- Keyboard: `↑`/`↓` move highlight, `Enter` runs highlighted, `Esc` or deleting the `/` closes. Mouse
  hover/click supported. Escapable, as required.
- Disabled states: each command exposes `enabled(ctx)`; disabled commands still render (greyed, with a
  hint) for discoverability — e.g. `/stop` only when a run is active, `/retry` only with a last message.
- Execution: selecting clears the `/` draft and calls `run(ctx)`, where
  `ctx = { boardId, taskId, activeRunId, lastUserText, refreshTask, resend, openHelp }`. A short toast
  confirms the outcome.
- New, isolated component `CommandMenu` (not tangled into `ChatTab`); commands declared in a
  `chatCommands` registry array — adding a command later is one entry.

### Files (frontend)
- Create `src/components/workspace/CommandMenu.tsx` — the palette UI (filter + keyboard nav over the registry).
- Create `src/lib/chat-commands.ts` — the `ChatCommand` type + `chatCommands` registry.
- Modify `src/components/workspace/ItemPanel.tsx` (`ChatTab`) — detect leading `/`, render `CommandMenu`,
  provide `ctx`, and **filter rendered events by `ChatClearedAt`** (in `useRunEvents`/`bubbles`).
- Modify `src/lib/api.ts` — add `tasks.clear`, `tasks.stop`, `tasks.compact`.
- Modify `src/lib/types.ts` — add `chatClearedAt?: string` to `AgentTask`.

## Backend

### Data model
- `AgentTask.ChatClearedAt` (`DateTimeOffset?`, `[JsonPropertyName("chatClearedAt")]`).

### Endpoints (TasksController)
- `POST /api/boards/{boardId}/tasks/{taskId}/clear` → `IChatService.ClearAsync` (or a small
  `TaskCommandService`): patch the three fields, return the updated task.
- `POST /api/boards/{boardId}/tasks/{taskId}/stop` → load run → POST workflow terminate → mark run
  `Cancelled` → return ok.
- `POST /api/boards/{boardId}/tasks/{taskId}/compact` → `CompactService.CompactAsync`.

### Workflows
- New HTTP trigger in `HttpTrigger.cs`: `POST pipelines/{instanceId}/terminate` →
  `durableClient.TerminateInstanceAsync(instanceId, "user /stop")`. (Mirrors `RaiseUserMessage`.)

### `/compact` summarization (the heavy piece)
`CompactService.CompactAsync(boardId, taskId)`:
1. Read chat `RunEvent`s since the last boundary (UserMessage/AgentMessage).
2. One Foundry `/responses` call with a fixed instruction: "Summarize the key decisions, state, and
   open items of this conversation in a short brief." (Direct call or via `IAgentRuntime`.)
3. Write the summary to `TaskBrief`; reset thread (`FoundryThreadId=null`) + set `ChatClearedAt=now`.
4. On any failure in steps 1–2: fall back to plain `/clear` semantics; return a flag so the UI can toast
   "Couldn't summarize — cleared instead." Never lose/corrupt state.

## Error handling
- All command endpoints are idempotent-ish patches; failures return non-2xx and the UI toasts an error,
  leaving state unchanged.
- `/stop` when no active run → no-op success.
- `/compact` summarization failure → graceful `/clear` fallback (above).

## Testing
- Frontend: registry `enabled`/`run` logic; `/`-trigger open/close + fuzzy filter + keyboard nav;
  events filtered by `chatClearedAt`.
- Backend: `clear` patch sets the three fields; `stop` calls terminate + marks Cancelled (mock durable
  client / HTTP); `compact` writes summary + resets (mock `IAgentRuntime`), and the failure→clear
  fallback.

## Scope

**In scope:** `CommandMenu` + `chatCommands` registry; `/`-trigger/search/keyboard UX; five commands;
`clear`/`stop`/`compact` endpoints + workflows `terminate` trigger; `ChatClearedAt` + boundary filtering;
`api.ts` methods.

**Phasing note:** `/compact` is the only LLM-dependent command; it can land as a second slice after
`/clear`+`/stop`+`/retry`+`/help` if we want the simple commands shipping first.

**Deferred (YAGNI):** command arguments/parameters, user-defined commands, a `/clear` undo button,
multi-step command flows, slash-commands outside the task chat.
