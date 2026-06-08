# Tectika — Agent Execution Brain (Design & Implementation Plan)

## Context

Tectika is "Monday.com for AI agents": users build boards of tasks, assign each task an AI **agent role**, wire dependencies between tasks, and run the whole thing. The vision (in [delegated-frolicking-dongarra.md](delegated-frolicking-dongarra.md) and the PRD) is that a user lays out a **full, correct plan** and the platform executes it *flawlessly, synchronized across agents, and safely*.

The UI, API, Cosmos models, Durable Functions skeleton, approvals, and SSE streaming already exist and largely work. But the **execution brain is only stubbed**:

- An "agent" today is **one single-shot Azure OpenAI chat-completion HTTP call** — no tools, no MCP, no memory/threads, no agentic loop. `FoundryAgentId`, `Tools`, `McpServers` are stored but unused.
- **Context** is naive string concatenation (system prompt + task text + upstream artifacts). No budgets, no retrieval, no project-wide awareness.
- **Orchestration** is a strictly **linear** loop over a flat step list. The real `UpstreamTaskIds`/`DownstreamTaskIds` task DAG is ignored — no parallelism, no joins, no loops.
- **Safety**: `AgentPermissions` exists but is **never enforced**; no OBO; no token/cost budgets; no loop caps; no retries.

This plan designs the brain that turns the wired board into correct, synchronized, safe execution.

## Locked design decisions (the contract)

| Dimension | Decision |
|---|---|
| **Runtime** | **Azure AI Foundry Agent Service** (`Azure.AI.Agents.Persistent`): server-side threads, multi-turn tool-calling loop, MCP, streaming. Replaces the chat-completion shim. |
| **Action surface** | Agents are **tool-using on real systems** (git, deploy, files, HTTP, MCP). |
| **Topology** | Execute the **board task graph**: parallel branches, fan-in joins, **plus bounded QA feedback loops** (cyclic edges). |
| **QA loops** | QA emits a **structured pass/fail verdict**; loop dev→QA up to `MaxIterations` (default 3); on cap exhaustion → **pause & escalate to a human**. |
| **Plan intake** | User wires tasks + agents + edges **manually**. The wired graph *is* the plan (no auto-decomposition). |
| **Context** | A **Context Manager**: shared project brief + direct upstream artifacts + on-demand retrieval, under a token budget with summarization of oversized inputs. |
| **Memory** | **Persistent Foundry thread per task** — QA retries reuse the thread, so the agent remembers prior attempts + feedback. |
| **Safety** | Sensitive tool calls run under the agent's **role managed-identity** but only after a **mandatory human approval gate**, enforced from `AgentPermissions`. OBO deferred. Plus per-run token/cost budgets + loop caps + per-tool audit. |
| **Execution substrate** | Agent tool/code actions run in **Azure Container Apps dynamic sessions** — ephemeral, Hyper-V-isolated sandbox, **one session per task-run** (reused across QA retries), auto-torn-down. Untrusted/LLM-generated code never runs in the Functions host. |
| **Action targets** | **Git** (GitHub, **PR-only**), **Azure resources** (control plane), **databases/data stores**, **external HTTP/SaaS APIs** — each a typed tool with its own auth + gating. |
| **External auth** | Azure targets → role MI + **RBAC scoped to the role's allowed resources**. Non-Azure (git / db / SaaS) → **Key Vault per-role secrets**, fetched under role MI **just-in-time** at exec time, injected into the sandbox per-command, **never persisted** in the image or artifacts. |

## Current architecture & the 5 tensions to resolve

Verified against the codebase. These are where the locked decisions collide with reality:

- **T1 — Edges/loops are NOT persisted.** The board persists only forward DAG pointers (`AgentTask.UpstreamTaskIds`/`DownstreamTaskIds`). Feedback/loop edges + labels live **only in browser localStorage** ([src/web/tectika-board/src/lib/board-context.tsx](../src/web/tectika-board/src/lib/board-context.tsx), back-edges recomputed in `CanvasView.tsx`). **Biggest gap — must be fixed first (Phase 0).**
- **T2 — Orchestration is per-task, not per-board.** Today one `WorkflowRun` == one `AgentTask` ([RunsController](../src/api/TectikaAgents.Api/Controllers/RunsController.cs)). A graph run spans many tasks → needs a board-level parent run.
- **T3 — Artifact versioning is race-unsafe.** [InvokeAgentActivity.cs](../src/workflows/Activities/InvokeAgentActivity.cs) does read-max-then-write with no ETag; safe only because execution is sequential. Parallel fan-out breaks it.
- **T4 — Two divergent agent runners.** [WorkflowAgentRunner.cs](../src/workflows/Services/WorkflowAgentRunner.cs) and [FoundryAgentService.cs](../src/api/TectikaAgents.Api/Services/FoundryAgentService.cs) duplicate the same shim. Both collapse into one `IAgentRuntime`.
- **T5 — Durable Functions replay constraint.** All Foundry/Cosmos/Service Bus IO must stay in **Activities**; the long agentic loop runs inside one Activity that yields back to the orchestrator only at approval gates.

## Target architecture

```
Board (Goal + MasterPlan)  ──►  BoardGraphOrchestrator  (fan-out/fan-in over task graph + TaskEdges)
                                       │  Task.WhenAll(ready nodes)
                                       ▼
                          TaskGraphNodeOrchestrator (per task: agent run, QA verdict, loop, approval)
                                       │  CallActivity
                                       ▼
   ContextManager ──► InvokeAgentActivity ──► IAgentRuntime (FoundryAgentRuntime)
   (brief+upstream+retrieval)                   │ persistent thread/agent, tool-loop, streaming
                                                ▼
                              ToolRegistry + MCP  ──►  PermissionGate ──► (sensitive? → approval gate → ResumeToolCall)
                                                          │  Activity = thin broker: gate → fetch creds JIT → run in sandbox
                                                          ▼
                              SandboxService ──► ACA dynamic session (per task-run, isolated, ephemeral)
                                                   └─ acts on: GitHub(PR-only) / Azure(MI+RBAC) / DB / HTTP
                                                          │  emits agent_thinking / tool_call SSE + AuditEntry
```

### 1. Foundry runtime layer
- New **`IAgentRuntime`** (`src/core/.../Interfaces/`): `EnsureAgentAsync(role)`, `EnsureThreadAsync(task)`, `RunTurnAsync(req)`. `AgentRunOutcome` carries terminal status (`Completed | RequiresApproval | Failed | BudgetExceeded`), artifact, token usage, and (when paused) the pending tool call(s) + Foundry thread/run ids.
- New **`src/workflows/Services/FoundryAgentRuntime.cs`** replaces `WorkflowAgentRunner.cs`, using **`Azure.AI.Agents.Persistent`** (`PersistentAgentsClient`, `PersistentAgentThread`, `ThreadRun`, `RequiredAction`/`SubmitToolOutputsAction`, `StreamingUpdate`). Maps: `SystemPrompt`→instructions, `Tools`/`McpServers`→tool defs, `ModelOverride ?? DefaultModel`→model. Persists the created agent id into the unused `AgentRole.FoundryAgentId`, and the per-task thread into a new `AgentTask.FoundryThreadId` (reused across QA retries).
- Streams: text deltas → emit `agent_thinking`; tool updates → emit `tool_call` (both `AgentEvent` types already defined but never emitted today). Delete `FoundryAgentService.cs`; swap DI in [src/workflows/Program.cs](../src/workflows/Program.cs).

### 2. Tool / MCP layer + mid-loop approval (hardest piece)
- `role.Tools` → local **function tools** in new `src/workflows/Tools/ToolRegistry.cs` (definition + C# handler that brokers to the sandbox — *this is "where the agent acts"*: real git/deploy/http/file side effects). `role.McpServers` → server-side **MCP tools** from new `Mcp:Servers` config, executed inside Foundry.
- Code-acting agents get a per-task **workspace** inside their ACA session (see §2a) via a `ToolContext` — the server-side analogue of the existing [CliBridgeManager](../src/api/TectikaAgents.Api/Services/CliBridgeManager.cs).
- **Pausing the loop for approval:** intercept each tool call before executing. Non-sensitive → run handler, submit tool output, continue, emit `tool_call` + audit. **Sensitive** (per `PermissionGate`) → persist resume coordinates `{threadId, runId, toolCallId, args}`, return `RequiresApproval` from the Activity. The node orchestrator then runs the **existing** approval pattern (`WriteApprovalActivity` + `WaitForExternalEvent`, generalized event name `approval-tool-{callId}`). A new `ResumeToolCallActivity` executes the tool under role MI and calls `SubmitToolOutputsToRun` on the still-open Foundry run; rejection submits a "DENIED" output so the agent replans. This works because the Foundry thread/run are **server-side persistent** — no Activity is held open for 48h.

### 2a. Execution substrate & action targets ("where agents act")

This is the security boundary, so it is fully specified.

**Substrate — Azure Container Apps dynamic sessions.** A new `src/workflows/Services/SandboxService.cs` manages a **session pool** of **custom-container** dynamic sessions (image carries `git`, `az` CLI, `dotnet`/`node`, db clients). On a task-run's first effectful tool call, `SandboxService` allocates a session and stores its id on a new `AgentTask.SandboxSessionId`; **the same session is reused across QA-loop retries** (persistent workspace, mirrors the persistent Foundry thread) and torn down on run completion/failure/idle-timeout. Sessions are Hyper-V isolated and ephemeral, so a misbehaving agent's blast radius is one session. **Network egress is allowlisted** per role (only the hosts/targets the role needs).

**The Activity is a thin broker, the sandbox does the work.** Tool handlers do *not* execute side effects in the Functions host. Per tool call the flow is: `PermissionGate` classify → (if sensitive) approval gate → **fetch scoped credential just-in-time** (role MI token for Azure RBAC, or Key Vault secret for non-Azure) → invoke the command in the ACA session via its exec API with the credential injected as a transient env var → capture stdout/exit → **scrub the credential** → submit the tool output back to the open Foundry run + write `AuditEntry`. No long-lived secret ever lands in the image, the workspace, or an artifact.

**Per-target tool + auth design** (each a typed tool in `ToolRegistry`):

| Target | Tools | Auth | Gated (sensitive)? |
|---|---|---|---|
| **Git — GitHub, PR-only** | `git_clone`, `create_branch`, `commit` (in-sandbox), `push_branch`, `open_pr` | GitHub credential (App-installation token preferred, else PAT) from **Key Vault per role**; restricted to the role's `AllowedRepos` | In-sandbox edits/commits = free; **`push_branch` + `open_pr` = gated**. Never push to protected branches. |
| **Azure resources** | `azure_whatif`, `azure_deploy`, `azure_resource_action` | **Role managed-identity + RBAC** scoped to the role's `AllowedAzureScopes` (specific resource groups) | `azure_whatif` (diff) = free; **`azure_deploy`/mutations = gated**, the what-if diff shown in the approval. |
| **Databases / data stores** | `db_query` (read), `db_execute` (write/migration) | Connection secret from **Key Vault per role** (or Entra auth for Azure SQL via MI) | Reads = free; **writes/migrations = gated**. |
| **External HTTP / SaaS** | `http_request` (host-allowlisted) | API key from **Key Vault per role**; only hosts in the role's `AllowedHosts` | GET = free; **mutating methods (POST/PUT/DELETE) = gated**. |

**Identity config lives on the role.** Extend `AgentRole` with an `IdentityConfig` block (the `identityConfig` the architecture doc already anticipated): `{ ManagedIdentityClientId?, KeyVaultSecretRefs: {logicalName → kvSecretName}, AllowedAzureScopes:[], AllowedRepos:[], AllowedHosts:[] }`. `PermissionGate` and `SandboxService` read this to decide what the role may touch and which credential to fetch.

**Mock parity:** a `MockSandbox` runs commands in a local temp workspace and no-ops/records external side effects, so the entire graph + approval + loop logic is testable with **no ACA, no Key Vault, no real git/Azure**.

### 3. Context Manager
- New `src/workflows/Services/ContextManager.cs` replaces `BuildMessages()`. Under a token budget (`Foundry:MaxInputTokens`) assembles: **(a) project brief** from new `Board.Goal` + `Board.MasterPlan` fields; **(b)** direct upstream artifacts via existing `WorkflowCosmosService.GetUpstreamArtifactsAsync`; **(c)** on-demand retrieval exposed as a `retrieve_artifact` function tool backed by a simple Cosmos keyword query (no embeddings in phase 1; Azure AI Search is a later upgrade).
- Oversized artifacts summarized once via a cheap model call, cached on a new `Artifact.Summary` field.

### 4. Graph orchestrator
- New **`TaskEdge`** model + `taskEdges` Cosmos container (PK `boardId`): `{FromTaskId, ToTaskId, EdgeKind(Dependency|QaFeedback), Condition, MaxIterations=3}`. The `connectTasks` endpoint + canvas UI persist these (fixes **T1**); forward `Upstream/DownstreamTaskIds` keep mirroring for board views.
- New **`BoardGraphOrchestrator.cs`**: load tasks+edges → compute **ready set** (all `Dependency` parents `Completed`) → `Task.WhenAll` of `CallSubOrchestratorAsync(TaskGraphNodeOrchestrator)` per ready task → join → loop until done. Captures parent artifact versions at dispatch for **join determinism**.
- **`TaskGraphNodeOrchestrator`** generalizes today's [TaskPipelineOrchestrator.cs](../src/workflows/Orchestrators/TaskPipelineOrchestrator.cs) (reusing its approval-gate pattern). QA nodes emit a structured `QaVerdict` (Foundry structured outputs). On **fail + under cap** → re-dispatch the dev node **reusing its thread** with the QA feedback; **at cap** → escalate to `role.EscalateTo` via the existing approval/wait flow. Existing approval plumbing (`WriteApprovalActivity`, `ApprovalsController.Respond`, `HttpTrigger.RaiseApprovalEvent`, SSE) reused unchanged except for the generalized event-name scheme.

### 5. Synchronization & shared-state safety
- **Artifact race (T3):** deterministic ids `{taskId}:{runId}:{iteration}` (duplicate = 409, not a silent second v-N) + `IfMatchEtag` on the task's `currentArtifactId` update.
- **Same external resource:** a **Durable Entity mutex** keyed by a tool-declared `ResourceKey` (e.g. `git:<repoUrl>`) serializes side-effecting tools across parallel branches.
- **Join determinism:** child captures parent artifact id+version at dispatch, so a later parent re-run (QA loop) can't mutate already-dispatched inputs.

### 6. Safety model
- New `src/workflows/Safety/PermissionGate.cs` finally **enforces** `AgentRole.Permissions` + `IdentityConfig`: classifies each tool call `Allowed | NeedsApproval | Denied` against `RequiresApprovalFor`/`CanPushCode`/`CanDeploy` **and** the resource allowlists (`AllowedRepos`/`AllowedAzureScopes`/`AllowedHosts`) — a call outside the allowlist is `Denied` outright. Azure actions run under role MI via existing `AppRegistrationIdentityService.GetServiceTokenAsync` (OBO stays deferred).
- **Credential handling:** secrets are fetched **just-in-time** under role MI, injected into the sandbox as transient env vars for one command, and **scrubbed** after; the sandbox image and any artifact/log are secret-free. Sandbox **egress is allowlisted** to the role's targets, so a compromised agent can't exfiltrate.
- **Per-run hard budget:** add `WorkflowRun.MaxTokens`/`MaxCostUsd`; `UpdateRunStatusActivity` already accumulates totals — add a trip check; pass `maxCompletionTokens` to the Foundry run. **Loop cap** = `TaskEdge.MaxIterations`.
- **Audit every tool call** by reusing `AuditEntry` + `WriteAuditActivity` + `AppendAuditAsync`.

### 7. Data model summary
- **New:** `TaskEdge` (+`taskEdges` container), `IAgentRuntime`/`AgentRunRequest`/`AgentRunOutcome`/`PendingToolCall`, `QaVerdict`, optional `BoardRun`, Durable Entity mutex, `SandboxService` + `ToolRegistry` + `PermissionGate`, the per-target tools (`git_*`, `azure_*`, `db_*`, `http_request`).
- **Changed (reuse existing fields):** `AgentRole` (activate `FoundryAgentId`/`Tools`/`McpServers`; **+`IdentityConfig`** = managed-identity id, Key Vault secret refs, `AllowedAzureScopes`/`AllowedRepos`/`AllowedHosts`), `AgentTask` (+`FoundryThreadId`, **+`SandboxSessionId`**), `Board` (+`Goal`, `MasterPlan`), `Artifact` (+`Summary`, ETag), `WorkflowRun` (+`MaxTokens`, `MaxCostUsd`, `BoardRunId`).
- **Config:** `FoundrySettings` (+`MaxInputTokens`, start using `ProjectName`), new `McpSettings`, `BudgetSettings`, **new `SandboxSettings`** (ACA session-pool endpoint, pool/image, exec + idle timeouts, egress policy), and **use the existing `KeyVaultSettings`** in core config.
- **Mock parity:** extend `InMemoryCosmosDbService` + add a `MockAgentRuntime` and `MockSandbox` so the entire graph/approval/loop/budget logic is testable with no Azure.

## Build sequencing

| Phase | Deliverable | Mock-testable |
|---|---|---|
| **0** | Persist `TaskEdge` (model, container, `connectTasks` + canvas UI migration off localStorage) — unblocks everything | ✅ |
| **1** | `IAgentRuntime` + `FoundryAgentRuntime` single-turn (no tools); collapse the two runners; emit `agent_thinking` | ✅ (MockAgentRuntime) |
| **2** | Context Manager (brief + upstream + retrieval + summarization); `Board.Goal`/`MasterPlan` | ✅ |
| **3** | `ToolRegistry` + MCP + `PermissionGate` + **mid-loop approval** (the hard part), tested with safe in-process tools | ✅ |
| **3a** | **`SandboxService` + ACA dynamic sessions** + `AgentTask.SandboxSessionId` + `AgentRole.IdentityConfig`; JIT-credential broker + egress allowlist + scrubbing | ✅ (MockSandbox) |
| **3b** | Per-target tools, rolled out in order: **Git (GitHub PR-only)** → **Azure (what-if/deploy)** → **DB** → **HTTP/SaaS**, each with its Key Vault / MI auth + gating | ✅ |
| **4** | `BoardGraphOrchestrator` fan-out/fan-in + board-level run (T2) | ✅ |
| **5** | QA loops: `QaVerdict`, max-iter, human escalation | ✅ |
| **6** | Sync & budgets: artifact ETag (T3), resource mutex, token/cost caps | ✅ |

Each phase is independently testable in **mock mode** before any Azure resources are involved.

## Verification

- **Unit/mock (every phase):** run the API + Workflows with `MockDatabase:Enabled=true` and `MockAgentRuntime`. Drive a board with a diamond DAG (A→{B,C}→D) + a dev↔QA loop; assert: B and C run in parallel, D joins both, the QA loop iterates then escalates at the cap, a sensitive tool call pauses at an approval and resumes on approve / replans on reject, and a token-budget trip halts the run.
- **SSE:** subscribe to `GET /api/runs/{runId}/stream`; confirm `agent_thinking`, `tool_call`, `approval_required`, `step_completed` events now actually flow (see [running-agentboard-for-visual-qa](../../../.claude/projects/-home-elimeshi-projects-repos-TectikaAgentEnvironment/memory/running-agentboard-for-visual-qa.md) for launching the app).
- **Audit/state:** assert `AuditEntry` rows for each tool call (with identity label + outcome), correct `WorkflowRun.TotalTokens`/`EstimatedCostUsd`, and one `Artifact` version per iteration with correct `InputContext.UpstreamArtifacts` and deterministic ids.
- **Substrate/auth (mock then real):** in mock, assert a `git` agent clones into the workspace, edits, and that `push_branch`/`open_pr` *pause at approval* and only run after approve; assert an `azure_deploy` shows a what-if diff in the approval; assert an out-of-allowlist repo/host/scope is `Denied`; assert no secret appears in any artifact, log, or the sandbox after a run. Then a real ACA-session smoke: one GitHub PR-only flow end-to-end with a Key Vault-sourced credential, and one Azure what-if under role MI.
- **Azure smoke (after mock passes):** point `FoundrySettings` at a real Foundry project, run one real agent turn with one MCP tool end-to-end.

## Top risks

1. **Beta SDK drift** — `Azure.AI.Projects`/`Agents.Persistent` are beta; pin versions, isolate behind `IAgentRuntime`.
2. **Pause/resume a Foundry run across a 48h approval** — relies on server-side thread/run persistence; validate the run stays resumable, design a re-open fallback.
3. **Durable replay correctness** — ready-set computation and loop state must be deterministic across replays (no IO in orchestrators).
4. **Parallel races** — artifact ETag + resource mutex must be in before enabling real fan-out.
5. **Frontend edge migration** — moving loop/label edges out of localStorage into `TaskEdge` needs a one-time migration of existing boards.
6. **Sandbox secret/egress discipline** — JIT credential injection + scrubbing + egress allowlist must be airtight; a leak here is the worst-case failure. Validate secrets never persist and egress is actually blocked.
7. **ACA dynamic-session lifecycle & cost** — pooling, idle timeouts, and guaranteed teardown matter for both correctness and spend; orphaned sessions are a cost/security risk.
8. **GitHub App setup** — PR-only via a GitHub App installation needs org-side configuration and scoped install tokens; PAT is the fallback but rotate via Key Vault.
