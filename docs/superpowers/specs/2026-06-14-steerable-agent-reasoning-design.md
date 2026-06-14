# Steerable Agent Reasoning & UX — Design

**Date:** 2026-06-14
**Status:** Approved (brainstorming) — pending spec review
**Author:** elimeshi + Claude

## Goal

Improve agent **reasoning quality** and the surrounding UX by reworking task execution into a
**Claude-Code-style steerable agentic tool-loop**. Today a task step is a single model call with a
"dump everything" prompt, no tools, and brittle markdown-marker output parsing. This redesign gives
each task its own instruction, a lean+explorable context model, a real multi-round tool-loop, and a
single conversation that a human can steer mid-run — surfaced through working **chat** and
**activity** views.

## Mental model (the spine)

A task is **one ongoing conversation**, and an agent works that conversation as a **steerable loop**.
Four things that were separate collapse into one loop with three views:

- **Conversation** = the Foundry thread (`AgentTask.FoundryThreadId`, already exists). Every turn —
  orchestrator-driven or human-typed — is a turn in that one thread.
- **A "run"** = the agent taking turns as a multi-round tool-loop: think → maybe call board-scoped
  exploration tools → get results → continue → until it writes/updates the task artifact.
- **Steerable** = between rounds the orchestrator drains any user messages and folds them in.
  Approval, interaction-required, and free-form steering are all "the run received a user message."
- **Chat** = the human door onto the conversation. Idle + message → start a run seeded by it.
  Running + message → inject, picked up at the next round boundary.
- **Activity** = the same run seen from the side: the event stream as a hierarchical, persisted timeline.

Three views onto one loop: the **artifact** (output), the **chat** (dialogue), the **activity** (trace).

## Decisions (locked during brainstorming)

| Topic | Decision |
|---|---|
| Prompt model | **Layered**: AgentRole prompt = persona/skills; new per-task `Prompt` = the specific job. Agent sees both. |
| Context floor | **Smart/budgeted**: full direct-upstream artifacts until a token budget, then summaries (+ pull full via tool). |
| Explore scope | **Current board only**. "Project" = the board. Agents cannot reach other boards. |
| Tool mechanism | **Client-side tool-loop inside Durable activities** (Approach A). `IProjectExplorer` is the seam to move to an MCP server later. |
| Chat execution | Chat **is** the run's conversation. Not a separate engine. |
| Steering shape | **Shape B**: orchestrator loops; each round is its own activity; user messages drained via `WaitForExternalEvent` between rounds. Picked up at next round boundary (not mid-model-call). |
| Activity scope | **Per-task**, in the task `ItemPanel`. |
| Activity store | **Persisted + replayable**, hierarchical: round = parent activity, tool calls = sub-activities (collapsible). |
| Chat → artifact | **Agent decides**: a turn that produces deliverable work writes a new artifact version; pure Q&A doesn't. |

## Data model

### `AgentTask` (extend)
- `Prompt` (string?, nullable) — per-task instruction, layered over role persona. Editable in UI.
- (existing `FoundryThreadId`, `TaskBrief`, `CurrentArtifactId`, `WorkflowRunId` reused unchanged.)

### `RunEvent` (new container `runEvents`, partition key `taskId`)
Persisted, replayable trace — single source of truth for **both** the Activity tab and the chat transcript.
- `Id`, `TaskId`, `RunId`, `Round` (int), `ParentId` (string?, null = round-level activity, else sub-activity)
- `Kind` (enum): `round_started` | `thinking` | `tool_call` | `tool_result` | `artifact_written` |
  `user_message` | `agent_message` | `interaction_required` | `approval_required` |
  `round_completed` | `run_completed` | `run_failed`
- `Title` (human headline, e.g. "Gathering data about the project"), `Detail` (string?)
- `ToolName`, `ToolArgsSummary`, `ResultSummary` (tool events)
- `TokenUsage`, `Timestamp`

### `PendingMessage` (new container `pendingMessages`, partition key `runId`)
Steering inbox. On a user message to a running task, the API raises the Durable external event **and**
records here so the loop drains deterministically across replay. Fields: `Id`, `RunId`, `TaskId`,
`Text`, `CreatedAt`, `Consumed`.

**Not adding** a separate `Message`/`Conversation` model. Conversation = Foundry thread (authoritative
for the model) mirrored into `RunEvent`s (authoritative for UI). One conversation, two representations.

## Context engine & agentic tool-loop

### Layered prompt assembly
- Foundry agent **instructions** carry the **role persona** (stable, provisioned per agent).
- Each turn's user content begins with the **per-task `Prompt`** in a clearly delimited block,
  followed by the assembled context. (Per-task prompt goes in user content, **not** the request-level
  `instructions` field, to avoid clobbering the provisioned persona.)

### Smart-budgeted context floor (`ContextManager`)
Stops dumping everything. New assembly:
- Always: project name/goal, task title + description + **per-task prompt**, **trimmed** TaskBrief
  (most-recent-N, not unbounded).
- Direct upstream: **full** artifact content until `FoundrySettings.MaxInputTokens`; beyond budget,
  each upstream's **summary** + note that full content is one `GetArtifact` away.
- Standing instruction: *"You can and should explore the board before significant work — search
  related tasks, read upstream and sibling artifacts, check the board overview. Don't guess when you
  can look."*

### Exploration tools — `IProjectExplorer` (new Core interface, board-scoped)
- `GetBoardOverview()` → task graph: titles, statuses, assignees, edges.
- `SearchTasks(query)` → tasks matching text across title/description/brief.
- `GetTask(taskId)` → one task's details + current artifact summary.
- `GetArtifact(taskId, version?)` → full artifact content.
- Implemented over `WorkflowCosmosService`, **board id baked in at construction** (no cross-board reach).

### Tool registration — VERIFIED Foundry contract (2026-06-14 live probe)
**Foundry rejects per-request `tools` in `agent_reference` mode** (HTTP 400 "Not allowed when agent
is specified"). Tools **must be attached to the agent definition** at provisioning time, flat shape:
`definition.tools = [{ type:"function", name, description, parameters }]` (nested `{function:{…}}` is
rejected). Therefore:
- `IAgentProvisioner.EnsureAgentAsync` attaches the **fixed Tectika tool schema** (explore + control
  tools below) to **every** agent definition. The toolset is identical across agents.
- `AgentInstructionsHash` includes a **tool-schema version** so changing the toolset republishes the
  agent version (today it hashes only prompt+model).
- The loop drives via the **conversation**: send input → Foundry returns `function_call` output items
  → execute → submit `function_call_output` items into the **same conversation** → repeat until a
  plain `message` is returned. (Round-trip verified end-to-end.)

### The loop (`RunTurnAsync` → multi-round, inside an activity)
1. Send input + `agent_reference` + `conversation` to Foundry `/responses` (NO per-request tools).
2. Reply has `function_call` item(s) → execute via `IProjectExplorer` (explore tools) or surface
   control tools to the caller, emit `tool_call`/`tool_result` events, submit `function_call_output`
   into the conversation, loop.
3. Reply is a plain `message` → parse, write/patch artifact, done.
4. Guardrails: max rounds + token budget; on exhaustion → clean failure (never partial-as-success).

### Control actions become tools (kills format-drift)
Replace today's `ParseAgentSections` string-matching with typed function calls:
- `request_human_input(question, options?)` ← was `## INTERACTION_REQUIRED`
- `request_approval(description)`
- `request_revision(reason)` ← was `## REVISION_NEEDED`
- `update_brief(text)` ← was `## Brief Update`
- `round_intent(text)` — one-line headline for the round (drives Activity parent + chat status line).

Keep a **thin fallback parser** for the old markers for one release as a safety net.

## Steerable orchestration (Shape B)

The orchestrator becomes a per-task **round loop** (pure/replay-safe; all side effects in the activity):

```
ensure thread + seed conversation (task prompt + context floor)
loop:
  StepResult = await RunAgentRoundActivity(...)        // one round: model + its tool calls + events
  drain: msg = WaitForExternalEvent("user_message", non-blocking)
         if msg → append as user turn (steering)
  switch StepResult.outcome:
    Continue            → loop
    NeedsInput/Approval → status=Awaiting…; msg = WaitForExternalEvent("user_message")  // BLOCKING
                          append reply, loop
    Done                → finalize artifact, break
    Failed/MaxRounds    → fail run, break
```

- **One external-event channel** (`user_message` on the run's instance id): non-blocking drain =
  steering; blocking wait = approval/interaction. One mechanism, three behaviors.
- **Run lifecycle = chat lifecycle:** chat-when-idle starts this orchestration seeded with the message;
  chat-when-running raises `user_message` to `WorkflowRunId`.
- **Crash safety:** rounds checkpointed; Foundry thread holds the conversation; restart resumes from the
  last completed round.
- **Non-blocking drain note (impl):** Durable has no literal `timeout=0`; use the held-external-event-task
  pattern (keep the `WaitForExternalEvent` task across iterations, check `IsCompleted`).
- Bonus: orchestrator is now loop-shaped, shrinking the later board-DAG fan-out work (roadmap T2).

## Chat wiring

- `POST /api/tasks/{taskId}/chat { text }`:
  - No active run → start task orchestration seeded with `text` (returns `runId`).
  - Active run → `RaiseEventAsync(workflowRunId, "user_message", text)`.
  - Always persist a `RunEvent{Kind=user_message}` immediately (instant transcript echo).
- Reply streams over existing SSE (`/api/runs/{runId}/stream`); `ChatTab` subscribes instead of its
  `setTimeout` mock and renders `thinking`/`tool_call`/`agent_message` as bubbles.
- Artifact on chat = **agent decides** (same finalize path as autonomous rounds).
- `ChatTab.chatThreads` localStorage mock replaced by persisted `RunEvent`s + live SSE.

## Activity tab

- Renders persisted `RunEvent` trace as a hierarchical timeline.
- **Parent = round** (`round_started`, `Title` from `round_intent`). **Children = round events**
  (`thinking`, `tool_call`/`tool_result`, `artifact_written`), collapsible.
- **Live + replay from one source:** live `RunEvent`s via SSE append; on reopen, fetch stored
  `RunEvent`s and render the same tree. No live/history divergence.
- Lives in the task `ItemPanel` (per-task), reusing the existing `activity` tab slot.

## API & event schema

- `POST /api/tasks/{taskId}/chat` — start-or-inject.
- `GET /api/tasks/{taskId}/events?sinceRound=&runId=` — persisted `RunEvent`s for replay.
- `PATCH /api/tasks/{id}` — accept `prompt`.
- `GET /api/runs/{runId}/stream` (SSE) — unchanged transport, richer payloads.
- Approval/interaction respond endpoints — reused as the blocking `user_message` channel (or thin wrappers).
- `AgentEvent` (extend, backward-compatible): add `Round`, `ParentId`, `Kind`, `Title`, `ToolName`,
  `ToolArgsSummary`, `ResultSummary`; new `Type` constants `tool_result`, `round_started`,
  `round_completed`, `user_message`, `agent_message`. **SSE event and persisted `RunEvent` share one
  shape** — `RunAgentRoundActivity` writes the `RunEvent` and broadcasts the same object, so live and
  stored are identical by construction.
- Frontend `lib/types.ts`: replace local `ChatTurn`/`ActivityEntry` with `RunEvent`-mirroring types;
  `ChatTab` filters to message/text kinds, `ActivityTab` renders the full tree.

## Infrastructure (CRITICAL — idempotency rule)

Per project rule, `infra/` must stay current and idempotent so the project recreates from scratch.
This design adds two Cosmos containers — **must** be added to `infra/main.bicep` (and any module):
- `runEvents` (partition key `/taskId`)
- `pendingMessages` (partition key `/runId`)
No new top-level Azure resources; no Foundry/region/quota changes.

## Testing

- `MockAgentRuntime` gains a **scripted tool-loop** (emits tool calls + control tools) so the full
  loop, steering, events, and chat are testable without Azure.
- Unit: context budgeting/fallback, `IProjectExplorer` board-scoping, control-tool parsing, fallback parser.
- Orchestration: round loop, non-blocking drain, blocking interaction/approval, max-rounds failure.
- Integration (mock): chat-when-idle starts run; chat-when-running injects; `RunEvent`s persisted and
  replay matches live.

## Build-time risk — RESOLVED (2026-06-14 live probe against proj-agentteam)

Verified directly: `agent_reference` mode **rejects** per-request `tools` (HTTP 400). Tools **must be
on the agent definition** (flat function shape); the `function_call → function_call_output` round-trip
via the conversation works end-to-end. The design above reflects the verified contract. See memory
`foundry-tool-calling-verified`. (Note: the agentteam Foundry resource lives only in the Visual Studio
Enterprise subscription — the deploy memory's endpoint is correct but it is NOT in CloudEdge-Rhenium.)

## Scope

**In scope:** (1) per-task `Prompt` + UI; (2) smart-budgeted `ContextManager` + `IProjectExplorer`;
(3) multi-round tool-loop + control-action tools; (4) Shape-B steerable orchestrator + unified
run/chat lifecycle; (5) `RunEvent` store + `/events` + `POST /chat`; (6) wire `ChatTab`/`ActivityTab`
to real data; (7) infra containers.

**Deferred (YAGNI / later phases):** board-DAG fan-out (T2; orchestrator merely becomes loop-shaped
here); MCP-hosted tools (Approach B / Phase 4 — `IProjectExplorer` is the seam); permission enforcement
& action tools (git/deploy); artifact ETag race-safety (T3); board-level activity feed; cross-board
exploration.

**Migration:** thin fallback parser for old `##` markers for one release; mock path keeps everything
testable offline.
