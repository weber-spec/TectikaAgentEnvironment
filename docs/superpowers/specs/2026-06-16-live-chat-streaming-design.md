# Live chat streaming + the "agent at work" experience — Design

**Date:** 2026-06-16
**Status:** Approved for planning
**Branch:** feat/chat-tab-improvements

## Problem

When a user runs a task (or sends a chat message), the agent works "under the hood" and
the chat shows almost nothing until the agent's final reply. Three concrete failures:

1. **No live visibility.** Agent activity (rounds, tool calls, artifacts) only reaches the
   chat *after* a round completes, and the chat renders only a subset of it. During the long
   model call (10–40s) the user sees nothing.
2. **The "working" indicator is weak and feels stuck.** A static animation reads as frozen
   after ~2–3s no matter how polished, because perceived progress comes from *new information*
   over time, not motion.
3. **The working state evaporates on navigation.** Send a message, the thinking dots show;
   leave the chat and return a second later and they're gone — the user's message sits alone
   with no sign the agent is still working. This is the worst offender.

## Goals

- Every real agent step appears in the chat as it happens, live.
- A premium "working" experience that is *structurally incapable of looking stuck*.
- The working state is derived from **server truth**, so it survives navigation/remount.
- Honesty: we never fabricate activity. Synthetic flavor is clearly flavor; concrete claims
  (checkmarks, counts, tool names) only ever come from real events.

## Non-goals

- Token-by-token streaming of the agent's final text. Foundry buffers the full response; the
  live edge + activity stream already deliver "alive," so token streaming is out of scope.
- Per-tool mid-flight streaming within a round (see Decision D1). Deferred, not designed-out.

## Root-cause analysis (grounded in code)

- **Batched emission.** `RunAgentRoundActivity.Run` builds and publishes *all* of a round's
  RunEvents only after `RunRoundAsync` returns (`RunAgentRoundActivity.cs:114-118`). The model
  call inside `FoundryAgentRuntime.RunRoundAsync` (`FoundryAgentRuntime.cs:235`) is the dominant
  silence; tool execution (`RoundExecutor.ExecuteOneRoundAsync`) is fast (board reads are
  sub-second). So the gap that needs covering is the model call, and it currently has *no*
  bracketing event at its start.
- **Local-state "thinking."** `AgentChat` computes `thinking` from `pending`/`sending`/
  `lastSentAt` local state (`ItemPanel.tsx:452-455`). On remount these reset to empty, so
  `thinking` becomes false even though the run is still going. The echoed `UserMessage` event
  reloads from Cosmos (so the user bubble reappears), but nothing tells the UI the agent is
  still working.
- **Plumbing already exists.** Events flow workflows → Service Bus → API SSE → browser, and
  persisted events are replayed on mount via `GET /tasks/{id}/events`. Task status (including the
  `InProgress` working flag) is streamed/polled into board-context. We render the existing events
  richly and derive "working" from the existing `task.status` signal; we build no new transport,
  emission, or backend state.

## Design

### A. The two-layer chat model (frontend)

The chat's running region is split into two layers:

- **History layer — permanent, truthful.** Each real event commits to a line that stays:
  - `ToolCall` → `✓ <verb> <object> · <result summary>` (e.g. `✓ Read uploader.ts · 180 lines`).
    A tool shown in-flight renders a spinner that resolves to a check. (Under Decision D1 the
    spinner and check arrive together at round end; the spinner→check transition is preserved in
    the component so per-tool streaming can light it up later with no UI change.)
  - `ArtifactWritten` → `✓ Wrote artifact v<n>`.
  - `AgentMessage` → the agent's answer bubble.
  - `UserMessage` → the user's bubble.
  - `RoundStarted`'s round-intent (when present) → a subtle section header, not a bubble.
- **Live edge — ephemeral, lively.** Exactly one active element at the bottom, shown whenever
  the run is working. It is fed by a priority cascade and never sits static (section C).

### B. Backend — no change required

During planning we confirmed the backend already provides everything the experience needs, so
this is a **frontend-only** change (Decision D3):

- **Live visibility already streams.** `RoundStarted`, `ToolCall`, `ArtifactWritten`, and
  `AgentMessage` are all published over SSE at round end (`RunAgentRoundActivity.cs:114-118`) and
  the chat already receives them — it only fails to *render* them richly. The fix is rendering,
  not transport.
- **The working signal already exists and is live-synced.** `task.status === 'InProgress'` is
  the authoritative "agent is working" flag the backend sets on run start and clears when work
  stops; board-context keeps it fresh via SSE + polling (`board-context.tsx:659`,
  `isTaskRunning`). The live edge is anchored on it — which also fixes the navigate-away bug,
  because it is server state, not React state.
- **Tokens already stream.** Each `RoundStarted` event carries that round's `tokenUsage`
  (`RunEventFactory.cs:23`); summing across events gives the live cumulative count.

The long model-call gap is covered exactly as in the validated demo: the live edge is already up
(from `task.status`) with rotating phrases + ticking timer, then each round's steps pop into
history when the round completes. Emitting a `RoundStarted` event at round *start* (the earlier
draft of this spec) would add only a cosmetic "round started" header before the first round
finishes, at the cost of Durable Functions changes, retry idempotency, and a workflows redeploy —
not worth it.

### C. Live edge mechanics (frontend module)

New `src/web/tectika-board/src/lib/thinking-phrases.ts` + a `useLiveEdge` hook.

- **Phrase pools** keyed by context: `thinking` (whimsical), `exploring`, `planning`,
  `testing`, `github`. A `tool→context` map classifies the most recent tool
  (`get_board_overview`/`search_tasks`/`get_task`/`get_artifact` → exploring; `update_brief`/
  `round_intent` → planning; GitHub tools → github; future test tools → testing). Default
  `thinking` during a bare model call — honest, since the agent has not yet acted.
- **Rotation:** swap every ~3s, chosen randomly without immediate repeat. A fresh real event
  preempts the current phrase (the live edge briefly reflects it, then it commits to history).
- **Elapsed timer:** smooth, always truthful. Counts from the last `UserMessage` timestamp
  ("how long you've waited"); falls back to `run.startedAt`.
- **Tokens:** the **real** cumulative token value, updated at round boundaries from event
  `tokenUsage`. No fake smooth climb in production — occasional honest jumps are fine; the timer
  is the smooth anchor.
- **Orb:** subtle presence glyph (as validated in the visual demo), not a spectacle.

### D. Working-state persistence

`working` is derived from server truth, not local state:

- Primary: `task.status === 'InProgress'`. This is the authoritative, live-synced signal the
  backend maps every working state onto (it flips to `AwaitingApproval`/`AwaitingInteraction`/
  `Review`/`Done`/`Failed` when the agent stops or pauses), already used by `isTaskRunning`.
- Bridge: an optimistic local `justSent` flag covers the brief window between "user sent" and
  `task.status` syncing to `InProgress`.
- The live edge therefore shows iff `task.status === 'InProgress' || justSent`, and hides
  automatically on any non-working status — including when an interaction/approval is pending,
  where the existing interaction UI takes over.

Because `task` comes from board-context (repopulated from the server on mount, kept fresh by SSE +
polling), returning to the chat re-derives the true working state and the live edge persists.

### E. Event → UI mapping

| RunEvent kind | Destination |
|---|---|
| `RoundStarted` | sets live-edge context; subtle round header (intent if known) |
| `ToolCall` | history line, spinner→check |
| `ArtifactWritten` | history line `✓ Wrote artifact v<n>` |
| `AgentMessage` | agent answer bubble |
| `UserMessage` | user bubble |
| `InteractionRequired` / `ApprovalRequired` | existing interaction UI; live edge hidden |
| `RunCompleted` / `RunFailed` (AgentEvent) | clears working state |

## Decisions

- **D1 — Within-round tool streaming: batch at round end (chosen).** All tool/artifact/message
  events publish together when the round finishes. Fast board reads feel instant; the model-call
  gap is covered by the live edge. Tradeoff: slow tools (GitHub push/PR, future test runs) show
  their spinner→check only at round end, not mid-flight. The history component keeps the
  spinner→check affordance so per-tool streaming can be added later with no UI rework.
- **D2 — Token display is honest.** Real cumulative tokens, summed from event `tokenUsage` at
  round boundaries; the elapsed timer is the smooth element. No synthesized counts.
- **D3 — Frontend-only (chosen).** All three problems are solved in the web app. The backend
  already streams round events and exposes the `task.status === 'InProgress'` working signal, so
  no Durable Functions / Cosmos / Service Bus changes and no workflows redeploy. Lower-risk and
  simpler. Cost: no "round started" header before round 1 completes (the live edge covers it).

## Files touched (frontend-only)

- `src/web/tectika-board/src/lib/thinking-phrases.ts` — new: phrase pools, `tool→context` map,
  and pure helpers (phrase picker, token sum, context-from-events).
- `src/web/tectika-board/src/components/workspace/LiveEdge.tsx` — new: the live-edge component +
  `useLiveEdge` hook (rotating phrase, elapsed timer, token count, presence orb).
- `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` — rework `AgentChat` running
  region into history layer + live edge; derive `working` from `task.status`; render the full
  activity stream (RoundStarted/ToolCall/ArtifactWritten/AgentMessage) as history.

## Testing

- **Frontend:** `npx tsc --noEmit`, `npx eslint`, `npm run build -- --webpack` (no JS test
  runner in this app). Pure helpers in `thinking-phrases.ts` are written to be unit-checkable by
  inspection (deterministic given inputs; the only randomness is phrase choice).
- **Live smoke (deployed):** send a chat message; confirm the live edge shows and rotates phrases
  with a ticking timer through the model-call gap, real steps commit to history as each round
  completes, and — critically — navigating away and back keeps the live edge present while
  `task.status === 'InProgress'`.

## Edge cases

- **Run already finished when chat opens:** no live edge; history replays from persisted events.
- **Activity retry (Durable):** stable event ids keep persisted rows and live stream idempotent.
- **Service Bus absent (dev):** no live SSE; history still loads from Cosmos via polling
  fallback (pre-existing behavior). The live edge appears only when run status reads active.
- **Multiple rapid user messages:** each commits a `UserMessage`; the live edge timer re-anchors
  to the latest.
- **AwaitingInteraction:** live edge hidden; interaction UI owns the surface.
