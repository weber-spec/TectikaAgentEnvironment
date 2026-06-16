# Chat improvements (round 2) — Design

**Date:** 2026-06-16
**Status:** Approved for planning (design confirmed via Q&A)
**Branch:** `feat/chat-improvements-2`, branched off the `feat/agent-requests-hitl` tip (these four items are independent of that feature but touch the same `ItemPanel.tsx`, so building on its tip avoids merge conflicts; merge agent-requests-hitl first, then this).

Four independent improvements to the task chat / activity UI.

## Item 1 — Remove the Updates tab

The **Updates** tab renders `comments`, which live only in browser `localStorage` (`cfg.comments` in `board-context.tsx`), are seeded with mock data (`seedCollaboration`), and are written by `addComment` — they never reach the backend, never sync between users, and are unrelated to the agent. With the **Chat** tab now the real task conversation, Updates is redundant and misleading. Remove it.

- `ItemPanel.tsx`: drop `'updates'` from the `Tab` type and the tab-button list; remove the `{tab === 'updates' && <UpdatesTab .../>}` render; delete the `UpdatesTab` component and the `renderMentions` helper (used only by it).
- Remove now-dead comment plumbing from `board-context.tsx` (`comments` field, `addComment`, the `comments` seeding) **only where it becomes unused** — `tsc`/`eslint` confirm. Keep `activity`/`logActivity` (used elsewhere) and the `collaboration.ts` lib untouched (just stop consuming its `comments`).
- Tabs become: **Chat · Activity · Details · CLI Bridge**.

## Item 2 — Keep the chat live (SSE push + poll backstop)

Today agent activity streams over SSE but **user messages do not** — `ChatService.EchoUserMessageAsync` persists the `UserMessage` `RunEvent` and never broadcasts it, so another user's message only appears via a reload. Two layers:

**(a) Real-time push (API).** Inject `SseConnectionManager` into `ChatService`; in `EchoUserMessageAsync`, after `CreateRunEventAsync`, broadcast `AgentEvent.FromRunEvent(savedEvent)`. Both participants are already subscribed to the run's SSE stream, so a message appears in ~network latency. The frontend already handles `run_event`s and dedupes by id, so no frontend change is needed to *receive* the push.

**(b) Poll backstop (web).** In `useRunEvents`, poll `api.tasks.events(boardId, taskId)` every ~4s (paused when the tab is hidden), merging results into state by id. This covers reconnects, tasks with no active run yet (so no SSE subscription), missed events, and **multiple API instances** — direct SSE broadcast only reaches clients on the same API instance, so the poll guarantees ≤4s convergence regardless.

Because the transcript is always fresh, there is no "stale chat" to send into; we do **not** block sends with a refresh-before-send (adds latency, can't win a true simultaneous race — a document-editing pattern, not a chat one). Concurrent sends both land and render in timestamp order.

> Multi-instance note: cross-instance *real-time* push would need Service-Bus fan-out (the path workflows already use). Out of scope now; the poll bounds staleness to 4s across instances.

## Item 3 — Collapsible activity rows in the chat

The in-chat history step (`HistoryStep` in `ItemPanel.tsx`) is a single truncated line, so a long tool/args/result is unreadable without switching to the Activity tab. Make each tool/artifact row **click-to-expand inline**:

- Collapsed: the current one-line summary + a chevron, shown only when there is expandable content (`toolArgsSummary` or `resultSummary` present).
- Expanded: the full, untruncated `toolName` + args + result, wrapped over multiple lines.
- Per-row `useState`; the `round_intent` header line stays as-is (not collapsible).

Frontend only.

## Item 4 — Descriptive activity titles (synthesized)

In the Activity tab, `RoundStarted` rows show `RunEvent.Title`, which is `"Round {n}"` whenever the agent didn't call the `round_intent` tool. Synthesize a descriptive title in `RunEventFactory.BuildRoundEvents` (workflows):

```
title = !blank(outcome.RoundIntent) ? outcome.RoundIntent
      : SynthesizeRoundTitle(outcome, round)

SynthesizeRoundTitle(outcome, round):
  if outcome.Kind == Final and not blank(outcome.FinalText):
      return Truncate(outcome.FinalText, 70)
  var tools = outcome.ToolCalls
      .Select(tc => tc.Name)
      .Where(n => n is not "round_intent" and not "update_brief")
      .Distinct()
      .Take(3)
  if tools.Any():
      return Capitalize(string.Join(", ", tools.Select(FriendlyVerb)))
  return $"Round {round + 1}"
```

`FriendlyVerb` (backend tool→verb map): `get_board_overview`→"read board", `search_tasks`→"searched board", `get_task`→"read task", `get_artifact`→"read artifact"; GitHub tools (name contains `github`/`branch`/`pr`/`push`/`commit`)→"used GitHub"; otherwise the raw tool name. Extracted as a `public static` helper so it is unit-testable.

This improves the Activity tab; it does not change the in-chat history (which derives its header from the `round_intent` ToolCall, not the `RoundStarted` parent).

## Files touched

- **web** `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` — remove Updates (Item 1); `useRunEvents` poll (Item 2b); `HistoryStep` collapse (Item 3).
- **web** `src/web/tectika-board/src/lib/board-context.tsx` — remove dead comment plumbing (Item 1) if unused.
- **api** `src/api/TectikaAgents.Api/Services/ChatService.cs` — broadcast user messages over SSE (Item 2a).
- **workflows** `src/workflows/Services/RunEventFactory.cs` — synthesized round titles (Item 4) + a `public static` title helper.
- **tests** `tests/TectikaAgents.Tests/` — unit tests for the title synthesis helper.

## Testing

- **Backend:** `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` (new title-helper tests + existing suite green); `dotnet build` of api + workflows.
- **Frontend:** `npx tsc --noEmit`, `npx eslint`, `npm run build -- --webpack`.
- **Live smoke (deployed):** (1) Updates tab gone; (2) two browsers on the same task chat → a message in one appears in the other within ~1s (push) and ≤4s if SSE missed; (3) a long activity row expands/collapses inline; (4) Activity-tab rounds show descriptive titles, not "Round n".
- **Deploy:** web + api + workflows.

## Edge cases

- **Item 2 merge:** `useRunEvents` merges fetched + live events by id (no duplicates); poll paused when `document.visibilityState === 'hidden'`.
- **Item 2 ordering:** truly simultaneous sends both persist and render in timestamp order — accepted.
- **Item 4 fallbacks:** a round with only meta tools (`round_intent`/`update_brief`) and no final text → `"Round {n}"`; a final round with empty text but tools → the tool summary.
- **Item 1:** verify no other consumer of `comments`/`addComment` exists before deleting (build/lint gate).
