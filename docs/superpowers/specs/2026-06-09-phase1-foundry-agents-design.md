# Phase 1 — Real Azure AI Foundry Agent Service agents — Design

> Status: **approved** (2026-06-09). Branch: `feat/phase1-foundry-agents`.
> Slice of the v3 plan ([plans/final_plan.md](../../../plans/final_plan.md)) **Phase 1**. The other 7
> phases (MCP/sandbox tools, approval proxy, board-graph orchestrator, QA loops,
> retrieval, GitHub App, budgets) are explicitly out of scope here.

## Goal

Replace the single-shot chat-completion shim with **real Azure AI Foundry Agent
Service agents**: create one persistent agent per role, a persistent thread per
task, run a real server-side turn, collapse the two divergent runners behind one
abstraction, and stream `agent_thinking` over SSE. Everything must remain fully
runnable in **mock mode with no Azure**.

## UX model (from the user)

- **Agents tab** = CRUD for `AgentRole` (name, system prompt, model). Saving an
  agent **eagerly** creates/updates the real Foundry agent and shows a sync state.
- **Boards tab** = create tasks and assign an existing agent via the already-present
  `AgentTask.Assignee {Type: Agent|Human, Id}` (when `Type==Agent`, `Id` is the role).
  A run uses the task's assignee — **no new field needed**.

## Current state (what exists today)

- `WorkflowAgentRunner` (concrete, chat-completions) is the only live path, called by
  [InvokeAgentActivity.cs](../../../src/workflows/Activities/InvokeAgentActivity.cs).
  `FoundryAgentService` (API) is registered but **unused**. No `IAgentRuntime`, no
  mock seam for agent invocation (T4).
- `AgentRole.FoundryAgentId` and `AgentTask.FoundryThreadId` exist but are never set.
- `ContextManager.BuildContextAsync` already assembles a chat **messages list**.
- Events: workflows → Service Bus `agent-events` → API `ServiceBusListenerService` →
  SSE (`GET /api/runs/{runId}/stream`). `agent_thinking`/`tool_call` are defined but
  not emitted in the live path.

## Approach (chosen: A — shared runtime library)

Interfaces + DTOs live in `core` (no SDK dependency). A new project
**`TectikaAgents.AgentRuntime`** holds the concrete `FoundryAgentRuntime` +
`MockAgentRuntime`/`MockAgentProvisioner`, referenced by both API and workflows.
This isolates the **beta** `Azure.AI.Agents.Persistent` SDK in one place (the plan's
"pin + isolate beta SDK" risk), avoids duplication, and shares the mock seam.

Rejected: (B) impl in `core` — leaks an Azure SDK dependency into the domain project;
(C) separate per-project impls — duplicated client/auth, two places for the beta SDK
to drift.

## Components

### 1. Interfaces & DTOs (`core`, no SDK dep)
- `IAgentProvisioner` (API / Agents tab):
  - `Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct)` —
    create if `FoundryAgentId` empty; update if prompt/model changed (compare a stored
    hash); returns `{ FoundryAgentId, Synced, Error? }`.
  - `Task DeleteAgentAsync(string foundryAgentId, CancellationToken ct)`.
- `IAgentRuntime` (workflows):
  - `Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct)` — create +
    persist `FoundryThreadId` if missing; reuse otherwise.
  - `Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct)`.
- DTOs:
  - `AgentRunRequest { AgentRole Role; AgentTask Task; string ThreadId; string UserMessage; int MaxCompletionTokens; string RunId; int Step; }`
  - `AgentRunOutcome { AgentRunStatus Status; string Content; ArtifactContentType ContentType; TokenUsage TokenUsage; string? BriefUpdate; string CompletionId; }`
  - `enum AgentRunStatus { Completed, Failed, BudgetExceeded }` — `RequiresApproval`
    is reserved for a later phase but **not** used now.
  - `AgentSyncResult { string? FoundryAgentId; bool Synced; string? Error; }`

### 2. `FoundryAgentRuntime` (shared lib) — implements both interfaces
- `PersistentAgentsClient(projectEndpoint, DefaultAzureCredential)` where
  `projectEndpoint` = new `FoundrySettings.ProjectEndpoint`.
- **EnsureAgent:** `CreateAgent(model = role.ModelOverride ?? DefaultModel,
  name = role.DisplayName, instructions = role.SystemPrompt)`; **no tools** in Phase 1.
  Update when a stored prompt+model hash changed.
- **EnsureThread:** `CreateThread()` → persist `AgentTask.FoundryThreadId`.
- **RunTurn:** add `UserMessage` to the thread → create run (with
  `maxCompletionTokens`) → **stream** updates: text deltas → `agent_thinking` events;
  on terminal status read the assistant message → `AgentRunOutcome` with real token
  usage. **No** `RequiredAction`/tool-output handling yet (agents carry no tools).
- Maps the agent's final text to an artifact exactly as today; `## Brief Update`
  parsing stays in `InvokeAgentActivity`.

### 3. Agents tab — eager lifecycle
- `AgentRolesController.Upsert` → persist role → `EnsureAgentAsync` → save
  `FoundryAgentId` + return sync state. `Delete` → delete role **and** Foundry agent.
- Frontend Agents tab ([src/web/.../app/agents/page.tsx](../../../src/web/tectika-board/src/app/agents/page.tsx)):
  small **synced ✓ / error** indicator from the returned role (light touch).

### 4. Boards tab → run
- Use existing `task.Assignee`. `RunsController.Start` derives a single-step pipeline
  from the task's assignee (`Type==Agent`) when no explicit pipeline is supplied;
  client-supplied pipelines still allowed.
- `InvokeAgentActivity`: `EnsureThreadAsync` → `ContextManager` builds the
  **user-message content** (the system prompt now lives on the agent, not in a system
  message) → `RunTurnAsync` → save artifact + parse TaskBrief (unchanged).

### 5. Collapse the two runners (resolves T4)
- Delete `FoundryAgentService.cs` (API) and `WorkflowAgentRunner.cs` (workflows).
  All agent calls go through `IAgentProvisioner` / `IAgentRuntime`.

### 6. Context Manager adaptation
- Add `Task<string> BuildUserContentAsync(role, task, board, upstreamArtifacts, ct)`
  returning the assembled **user content string** (project brief + task + upstream
  artifacts + TaskBrief) **without** a system message. `InvokeAgentActivity` switches
  to it; the old `BuildContextAsync` (messages-list) is removed once it has no callers.
  Minimal change; no retrieval/embeddings.

### 7. Config + infra (honors the idempotency rule)
- `FoundrySettings += ProjectEndpoint (string), MaxCompletionTokens (int, default 4096)`.
- `infra/modules/foundry.bicep`: output the **project endpoint**
  `https://<customSubDomain>.services.ai.azure.com/api/projects/<project>`.
- Wire `Foundry__ProjectEndpoint` env into the API (`containerapps.bicep`) and the
  Function App (`functionapp.bicep`); `deploy.ps1` passes it through. All in the same
  change; re-validate `az bicep build`.

### 8. Mock parity (no-Azure testability)
- `MockAgentRuntime` + `MockAgentProvisioner`: deterministic content, fake ids, emit
  `agent_thinking`/`step_*` events. Selected by a dedicated `Foundry:UseMock` flag that
  **defaults to `MockDatabase:Enabled`** (so mock-DB runs get mock agents automatically,
  but the two can be decoupled). With it on, create-agent → assign → run → artifact →
  SSE runs locally with no Azure.

### 9. SSE / events
- `agent_thinking` now actually flows from `RunTurn` streaming; `step_started`/
  `step_completed` unchanged. `tool_call` stays deferred (no tools until Phase 4).

### 10. Errors & budgets
- `maxCompletionTokens` per run from config. Run failure → `AgentRunOutcome.Failed` →
  activity throws → existing failure path (`WriteAuditActivity`, status `Failed`).
  `WorkflowRun.MaxTokens`/cost accumulation in `UpdateRunStatusActivity` stays as-is.

## Out of scope (deferred to later phases)
MCP server / ACA sandbox & real tools; `tool_call` execution; approval proxy &
`PermissionGate`; `BoardGraphOrchestrator` / board-run (T2); QA loops & escalation;
context retrieval/embeddings & summarization; GitHub App connect; artifact ETag (T3);
full budget enforcement.

## Verification
- **Mock E2E (no Azure):** create an agent in the Agents tab → shows synced; create a
  task, assign the agent; `POST /api/runs/start` → artifact produced; `agent_thinking`
  + `step_completed` stream over SSE; `AgentTask.FoundryThreadId` persisted; running
  the task again accumulates `TaskBrief`.
- **Agent CRUD:** upsert → mock provisioner returns `Synced=true` with an id; delete →
  agent removed.
- **Azure smoke (after mock):** real Foundry project — one real agent created, one
  thread, one real turn returns an artifact with real token usage.

## Risks
- **Beta SDK drift** — pin `Azure.AI.Agents.Persistent`; all usage behind the runtime lib.
- **Project endpoint correctness** — validate `services.ai.azure.com/api/projects/<proj>`
  resolves for our `AIServices` account + project; this is the Phase-1 Azure smoke gate.
- **Durable replay** — all Foundry IO stays inside `InvokeAgentActivity` (an Activity),
  never in the orchestrator.
