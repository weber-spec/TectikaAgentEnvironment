# Tectika — Agent Execution Brain (Unified Plan v3)

> Merge of **plan-eli** (full architecture: topology, safety, sync, context) and **plan-mordechay** (concrete MCP server, GitHub connect flow, TaskBrief). Decisions taken jointly with the user.

## Context

Tectika is "Monday.com for AI agents": a user wires a board of tasks, assigns each an AI **agent role**, connects dependencies (incl. QA feedback loops), and runs it. Goal: a **full, correct, manually-wired plan executes flawlessly — synchronized, safe, no flaws**.

Today the **execution brain is stubbed**: an "agent" is one single-shot chat-completion (no tools, no threads, no loop); context is naive string concat; orchestration is strictly linear (the real task DAG is ignored); `AgentPermissions` is never enforced; there is no execution environment at all. This plan builds the brain.

## Locked decisions (the contract)

| Dimension | Decision | From |
|---|---|---|
| **Runtime** | Azure AI Foundry Agent Service (`Azure.AI.Agents.Persistent`): server-side threads, tool-loop, MCP, streaming. Replaces the chat-completion shim. | both |
| **Action surface** | Tool-using on **real systems** (git, run/build, Azure, DB, HTTP). | both |
| **Topology** | Execute the board **task DAG**: parallel branches, fan-in joins, **+ bounded QA feedback loops**. | eli |
| **QA loops** | QA emits a structured pass/fail verdict; loop dev→QA up to `MaxIterations` (default 3); on cap → **pause & escalate to a human**. | eli |
| **Plan intake** | User wires tasks + agents + edges **manually**; the wired graph *is* the plan. | eli |
| **Context** | A **Context Manager** = shared project brief + **TaskBrief running log** + direct upstream artifacts + on-demand retrieval, under token budget with summarization. | eli + **mordechay (TaskBrief)** |
| **Memory** | **Persistent Foundry thread per task** (reused across QA retries) + cumulative **TaskBrief** per task. | both |
| **Execution substrate** | **Hybrid:** a self-hosted **MCP server** (mordechay's concrete tools) hosted **inside an ACA dynamic session** (Hyper-V isolated, fast, ephemeral). | **hybrid decision** |
| **Substrate granularity** | **One shared session per board-run** (single working tree); file/git mutations **serialized via a workspace mutex**; non-repo work runs parallel. | **mordechay + eli mutex** |
| **Sensitive actions** | Gated by a **mandatory approval gate** via an **approval proxy** on sensitive MCP tools; executed under role identity only after approval. OBO deferred. | eli (adapted) |
| **GitHub** | **GitHub App per-board** + **PR-only** + per-role `AllowedRepos`. Concrete per-board connect flow/UI from mordechay. | **mordechay flow + eli security** |
| **External auth** | Azure → role MI + scoped RBAC. Non-Azure (git App token / db / SaaS) → **Key Vault per-role secrets**, fetched **JIT**, injected per-command, **scrubbed**, never persisted. | eli |

## The 5 pre-existing tensions to resolve

- **T1 — Edges/loops are NOT persisted** (only browser localStorage in [board-context.tsx](../../projects/repos/TectikaAgentEnvironment/src/web/tectika-board/src/lib/board-context.tsx)). The DAG/QA-loop topology has no backend truth. **Fix first (Phase 0).**
- **T2 — Orchestration is per-task, not per-board** ([RunsController](../../projects/repos/TectikaAgentEnvironment/src/api/TectikaAgents.Api/Controllers/RunsController.cs)). A graph run spans many tasks → needs a board-level run.
- **T3 — Artifact versioning is race-unsafe** ([InvokeAgentActivity.cs](../../projects/repos/TectikaAgentEnvironment/src/workflows/Activities/InvokeAgentActivity.cs): read-max-then-write, no ETag). Parallel fan-out breaks it.
- **T4 — Two divergent runners** ([WorkflowAgentRunner.cs](../../projects/repos/TectikaAgentEnvironment/src/workflows/Services/WorkflowAgentRunner.cs) + [FoundryAgentService.cs](../../projects/repos/TectikaAgentEnvironment/src/api/TectikaAgents.Api/Services/FoundryAgentService.cs)) → collapse into one `IAgentRuntime`.
- **T5 — Durable replay constraint** — all Foundry/Cosmos/Service Bus IO stays in Activities; the agentic loop is one Activity that yields to the orchestrator only at approval gates.

## Target architecture

```
Board (Goal + MasterPlan + Repo)
   │
   ▼
BoardGraphOrchestrator ── starts ──► SandboxService ► ACA dynamic session (per board-run)
   │  fan-out/fan-in over TaskEdges                     └─ runs src/agent-container/ MCP server
   │  Task.WhenAll(ready nodes)                            (git/file/run_command/create_pr tools)
   ▼
TaskGraphNodeOrchestrator (per task: agent turn, QA verdict, loop, approval)
   │
   ▼
ContextManager (project brief + TaskBrief + upstream + retrieval, budgeted)
   │
   ▼
FoundryAgentRuntime (IAgentRuntime) ── persistent thread per task, tool-loop, streaming
   │   tools = MCP(server-side, in session) + retrieve_artifact(function)
   ▼
PermissionGate ─ sensitive? ─► approval proxy ─► WriteApproval + WaitForExternalEvent
                                                        └─ approved → ResumeToolCallActivity → MCP /execute-approved (role cred, JIT)
   │  emits agent_thinking / tool_call SSE + AuditEntry per call
   ▼
git push / create_pr (PR-only) · azure_deploy · db_execute · http  — all under workspace mutex
```

---

## Components

### 1. Foundry runtime (`IAgentRuntime`)
- New **`IAgentRuntime`** (`src/core/.../Interfaces/`): `EnsureAgentAsync(role)`, `EnsureThreadAsync(task)`, `RunTurnAsync(req)`. `AgentRunOutcome` = `{ status: Completed|RequiresApproval|Failed|BudgetExceeded, artifact, tokenUsage, briefUpdate, pendingToolCall? }`.
- New **`src/workflows/Services/FoundryAgentRuntime.cs`** (replaces `WorkflowAgentRunner.cs`; delete `FoundryAgentService.cs`) using `Azure.AI.Agents.Persistent` (`PersistentAgentsClient`, `PersistentAgentThread`, `ThreadRun`, `RequiredAction`/`SubmitToolOutputsAction`, `StreamingUpdate`).
- Maps `SystemPrompt`→instructions, `ModelOverride ?? DefaultModel`→model, role tools + the **session MCP endpoint** → tool defs. Persists agent id into `AgentRole.FoundryAgentId`, thread id into `AgentTask.FoundryThreadId` (reused on QA retries).
- Streams text→`agent_thinking`, tool updates→`tool_call` (both `AgentEvent` types exist but are never emitted today). `AgentRolesController.Upsert` creates/updates the Foundry agent (mordechay).

### 2. Execution substrate — MCP server on ACA dynamic session (hybrid)
- **`src/agent-container/`** (new, from mordechay): a Node/TS **MCP server** (MCP SDK) + `Dockerfile`. Image carries `git`, `az` CLI, build runtimes, db clients. On session start: `git clone --depth=1` of the board's repo into the workspace. Tools (concrete):

  | Tool | Sensitive? |
  |---|---|
  | `read_file`, `list_files`, `git_status` | no |
  | `run_command(cmd,cwd)` (tests/build) | no (read/build); writes → mutex |
  | `write_file`, `git_commit`, `git_create_branch` | no (local; mutex-serialized) |
  | **`git_push`**, **`create_pr`** | **yes → approval proxy** |
  | `azure_whatif`, `db_query`, `http GET` | no |
  | **`azure_deploy`/`azure_resource_action`**, **`db_execute`**, **mutating `http`** | **yes → approval proxy** |

- **`SandboxService`** (`src/workflows/Services/`) manages the ACA dynamic session lifecycle (custom-container) **per board-run** — generalizes mordechay's `StartDevContainerActivity`/`StopDevContainerActivity`. Stores `McpEndpointUrl` + session id on the board-run; tears down on completion/failure/idle. **Egress allowlisted** per board/role.
- **Feasibility note (risk #1):** validate that an ACA dynamic session can host a network-reachable MCP server for the run's lifetime. **Fallback = ACI per-run exactly as mordechay specced** — so the path is de-risked.

### 3. Sensitive-tool approval proxy (the hard part)
Foundry calls MCP tools directly, so to enforce human approval mid-loop without blocking a tool call for 48h:
- Sensitive tools (`git_push`, `create_pr`, `azure_deploy`, `db_execute`, mutating `http`) **do not execute on call**. The MCP server records the proposed action (branch, diff, target) and returns `PENDING_APPROVAL:{actionId}` to the agent, and signals our API (`approval_required`).
- `TaskGraphNodeOrchestrator` runs the **existing** gate (`WriteApprovalActivity` + `WaitForExternalEvent`, event `approval-tool-{actionId}`), reusing `ApprovalsController.Respond` + `HttpTrigger.RaiseApprovalEvent` + SSE unchanged.
- On **approve** → new **`ResumeToolCallActivity`** calls the MCP server's authenticated `/execute-approved` with the action id + the **role credential fetched JIT** (GitHub App installation token / role MI / KV secret), executes, returns the result into a new agent turn (same thread). On **reject** → the agent gets "DENIED, replan."
- **`PermissionGate`** (`src/workflows/Safety/`) finally **enforces** `AgentRole.Permissions` + `IdentityConfig`: classifies `Allowed | NeedsApproval | Denied` against `RequiresApprovalFor`/`CanPushCode`/`CanDeploy` **and** allowlists (`AllowedRepos`/`AllowedAzureScopes`/`AllowedHosts`); out-of-allowlist = `Denied`.

### 4. Context Manager (+ TaskBrief)
- **`src/workflows/Services/ContextManager.cs`** replaces `BuildMessages()`. Under `Foundry:MaxInputTokens` assembles: **(a)** project brief from new `Board.Goal` + `Board.MasterPlan`; **(b)** the task's **`TaskBrief`** running log (mordechay); **(c)** direct upstream artifacts (`GetUpstreamArtifactsAsync`); **(d)** on-demand `retrieve_artifact` function tool (simple Cosmos keyword query; embeddings later). Oversized artifacts summarized once → cached on new `Artifact.Summary`.
- **TaskBrief loop (mordechay):** the agent ends each turn with a `## Brief Update` one-liner; `InvokeAgentActivity` parses it and appends `[role, runId[:6], step]: …` to `AgentTask.TaskBrief`. Cleared when the task → Done.

### 5. Graph orchestrator (DAG + QA loops)
- New **`TaskEdge`** model + `taskEdges` Cosmos container (PK `boardId`): `{FromTaskId, ToTaskId, EdgeKind(Dependency|QaFeedback), Condition, MaxIterations=3}` — persisted by `connectTasks` + the canvas UI (**fixes T1**); forward `Upstream/DownstreamTaskIds` keep mirroring for board views.
- New **`BoardGraphOrchestrator.cs`**: start the session (SandboxService) → load tasks+edges → compute **ready set** (all `Dependency` parents `Completed`) → `Task.WhenAll(CallSubOrchestratorAsync(TaskGraphNodeOrchestrator))` → join → loop until done → stop session (in `finally`). Captures parent artifact versions at dispatch (**join determinism**).
- **`TaskGraphNodeOrchestrator`** generalizes today's [TaskPipelineOrchestrator.cs](../../projects/repos/TectikaAgentEnvironment/src/workflows/Orchestrators/TaskPipelineOrchestrator.cs). QA nodes emit a structured `QaVerdict`. On **fail + under cap** → re-dispatch the dev node **reusing its thread** with QA feedback; **at cap** → escalate to `role.EscalateTo` via the approval/wait flow.

### 6. Synchronization & safety
- **Workspace mutex:** a Durable Entity keyed on the board-run session serializes every file/git-mutating tool call on the shared working tree; read/think/HTTP/DB-read run in parallel. (Reconciles "parallel DAG" with "one shared container.")
- **Artifact race (T3):** deterministic ids `{taskId}:{runId}:{iteration}` (dup = 409) + `IfMatchEtag` on the task's `currentArtifactId`.
- **Credentials:** JIT under role MI, injected as transient env vars for one command, scrubbed after; secret-free image/artifacts/logs.
- **Budgets:** `WorkflowRun.MaxTokens`/`MaxCostUsd` (trip check in `UpdateRunStatusActivity`, already accumulates totals) + `maxCompletionTokens` on the Foundry run; loop cap = `TaskEdge.MaxIterations`.
- **Audit:** every tool call → `AuditEntry` via `WriteAuditActivity`/`AppendAuditAsync`.

### 7. GitHub App per-board connect (mordechay flow + eli security)
- **`Board`** + `RepoUrl`, `RepoOwner`, `RepoName`, `DefaultBranch`(="main"), `GitHubInstallationId` / `GitHubTokenSecretName`.
- **`GitHubController`** (new): `GET …/github/connect` (App install URL), `…/github/callback`, `…/github/status`, `DELETE …/github` (disconnect → KV cleanup); board delete → cleanup.
- Git tools use a **short-lived App installation token** fetched JIT (App private key in Key Vault), scoped to `AllowedRepos`; **PR-only** — never push to protected branches; `create_pr` is the gated action.
- **Frontend** ([app/boards/page.tsx](../../projects/repos/TectikaAgentEnvironment/src/web/tectika-board/src/app/boards/page.tsx)): replace `window.prompt` with a 2-step modal (Name/Description → optional "Connect GitHub"); board card shows `org/repo ✓` badge.

### 8. Data-model & config summary
- **New models/containers:** `TaskEdge`(+`taskEdges`), `IAgentRuntime`/`AgentRunRequest`/`AgentRunOutcome`/`PendingToolCall`, `QaVerdict`, `BoardRun`, workspace-mutex entity.
- **Changed (reuse fields):** `AgentRole` (activate `FoundryAgentId`/`Tools`/`McpServers`; +`IdentityConfig`{MI id, KV refs, `AllowedRepos`/`AllowedAzureScopes`/`AllowedHosts`}); `AgentTask` (+`FoundryThreadId`, +`TaskBrief`); `Board` (+`Goal`,`MasterPlan`,repo fields); `Artifact` (+`Summary`,ETag); `WorkflowRun`/`BoardRun` (+`SessionId`,`McpEndpointUrl`,`MaxTokens`,`MaxCostUsd`,`BoardRunId`).
- **Config:** `FoundrySettings`(+`MaxInputTokens`, use `ProjectName`), new `McpSettings`, `SandboxSettings`(ACA pool/image/timeouts/egress), `GitHub`(App id + private-key secret), `Azure`(subscription/RG/location), `BudgetSettings`, existing `KeyVaultSettings`.
- **Mock parity:** `InMemoryCosmosDbService` + `MockAgentRuntime` + `MockSandbox`/`MockMcp` + mock GitHub → entire graph/approval/loop/budget logic testable with **no Azure**.

---

## Build sequencing (each phase mock-testable)

| Phase | Deliverable | Source |
|---|---|---|
| **0** | `TaskEdge` persistence (model, container, `connectTasks` + canvas migration off localStorage) — unblocks the DAG | eli (T1) |
| **1** | `IAgentRuntime` + `FoundryAgentRuntime` single-turn + per-task threads; collapse the two runners; `AgentRolesController` create/update agent | both |
| **2** | **TaskBrief** (quick, standalone) + Context Manager (brief + upstream; retrieval/summarize sub-step); `Board.Goal`/`MasterPlan` | mordechay + eli |
| **3** | **GitHub App per-board** connect flow (controller, board fields, 2-step modal, badge, KV) | mordechay + eli |
| **4** | **ACA session + `src/agent-container/` MCP server** per board-run; `SandboxService` start/stop; **non-sensitive** tools end-to-end | mordechay on ACA |
| **5** | `PermissionGate` + **approval proxy** for sensitive tools (`git_push`/`create_pr`/`azure_deploy`/`db_execute`/mutating http) — mid-loop approval | eli (adapted) |
| **6** | `BoardGraphOrchestrator` fan-out/fan-in + board-run (T2) + **workspace mutex** | eli |
| **7** | QA loops: `QaVerdict`, max-iter, human escalation | eli |
| **8** | Sync & budgets: artifact ETag (T3), token/cost caps | eli |

## Verification

- **Mock (every phase):** API + Workflows with `MockDatabase:Enabled=true` + `MockAgentRuntime` + `MockMcp`. Drive a diamond DAG (A→{B,C}→D) + a dev↔QA loop: assert B/C parallel, D joins both, QA loop iterates then escalates at cap, a sensitive tool **pauses at approval** and resumes on approve / replans on reject, a token-budget trip halts the run, and the **workspace mutex** serializes two parallel writes.
- **TaskBrief:** run a task twice → `TaskBrief` accumulates; task → Done → `TaskBrief` cleared.
- **GitHub + container (mordechay's E2E):** connect a board to a repo; run → ACA session/ACI created (and torn down); "read README" (read_file via MCP), "write+run test" (write+run_command), "open PR" → PR exists; out-of-allowlist repo = `Denied`; no secret in any artifact/log/session.
- **SSE:** `GET /api/runs/{runId}/stream` shows `agent_thinking`/`tool_call`/`approval_required`/`step_completed` actually flowing (launch per [running-agentboard-for-visual-qa](../../.claude/projects/-home-elimeshi-projects-repos-TectikaAgentEnvironment/memory/running-agentboard-for-visual-qa.md)).
- **Azure smoke (after mock):** real Foundry project + one real GitHub PR-only flow with a KV App token + one Azure what-if under role MI.

## Top risks

1. **MCP-on-ACA hosting** — can an ACA dynamic session host a reachable MCP server for the run? Validate early; **fallback = ACI per-run (mordechay's original)**.
2. **Beta SDK drift** — `Azure.AI.Agents.Persistent`/`Projects` are beta; pin + isolate behind `IAgentRuntime`.
3. **Approval proxy correctness** — pausing/resuming a sensitive action out-of-band while the Foundry thread stays coherent; validate the resume-into-thread path.
4. **Parallel + shared tree** — workspace mutex + artifact ETag must land before real fan-out (Phase 6 gates on Phase 8 mechanisms for unsafe combos).
5. **Frontend edge migration** — moving loop/label edges from localStorage into `TaskEdge` needs a one-time board migration.
6. **Secret/egress discipline** — JIT injection + scrubbing + egress allowlist must be airtight; a leak here is worst-case.
7. **ACA/ACI lifecycle & cost** — guaranteed teardown + idle timeouts; orphaned sessions are a cost/security risk.
8. **GitHub App setup** — org-side install + scoped installation tokens; rotate the App private key via Key Vault.

---
---
<div dir="rtl">

# טקטיקה — מוח ההרצה של הסוכנים (תוכנית מאוחדת v3)

> מיזוג של **plan-eli** (ארכיטקטורה מלאה: טופולוגיה, בטיחות, סנכרון, קונטקסט) ו-**plan-mordechay** (MCP server קונקרטי, flow חיבור ל-GitHub, TaskBrief). ההחלטות התקבלו במשותף עם המשתמש.

## רקע

טקטיקה היא "Monday.com לסוכני AI": המשתמש מחווט לוח של משימות, מקצה לכל אחת **תפקיד סוכן** (agent role), מחבר תלויות (כולל לולאות QA), ומריץ. המטרה: **תוכנית מלאה ונכונה שחווטה ידנית מתבצעת ללא דופי — מסונכרנת, בטוחה, ללא פגמים**.

כיום **מוח ההרצה מקובע (stub)**: "סוכן" הוא קריאת chat-completion בודדת (בלי כלים, בלי threads, בלי לולאה); הקונטקסט הוא שרשור מחרוזות נאיבי; התזמור לינארי לחלוטין (ה-DAG האמיתי של המשימות מתעלמים ממנו); `AgentPermissions` לא נאכף לעולם; ואין סביבת הרצה כלל. התוכנית הזו בונה את המוח.

## ההחלטות הנעולות (החוזה)

| ממד | החלטה | מקור |
|---|---|---|
| **Runtime** | Azure AI Foundry Agent Service (`Azure.AI.Agents.Persistent`): threads בצד-שרת, לולאת-כלים, MCP, streaming. מחליף את ה-chat-completion shim. | שתיהן |
| **משטח פעולה** | כלים הפועלים על **מערכות אמיתיות** (git, run/build, Azure, DB, HTTP). | שתיהן |
| **טופולוגיה** | הרצת ה-**DAG של המשימות**: ענפים מקבילים, fan-in joins, **+ לולאות QA חסומות**. | eli |
| **לולאות QA** | QA מחזיר verdict מובנה pass/fail; לולאת dev→QA עד `MaxIterations` (ברירת מחדל 3); במיצוי → **עצירה והסלמה לאדם**. | eli |
| **קליטת התוכנית** | המשתמש מחווט משימות + סוכנים + edges **ידנית**; הגרף המחווט *הוא* התוכנית. | eli |
| **קונטקסט** | **Context Manager** = project brief משותף + **לוג TaskBrief** + ארטיפקטים ישירים מלמעלה + retrieval לפי דרישה, תחת תקציב טוקנים עם summarization. | eli + **mordechay (TaskBrief)** |
| **זיכרון** | **thread קבוע per-task** (נשמר בין איטרציות QA) + **TaskBrief** מצטבר per-task. | שתיהן |
| **סביבת הרצה** | **היברידי:** **MCP server** עצמאי (הכלים הקונקרטיים של מרדכי) המתארח **בתוך ACA dynamic session** (בידוד Hyper-V, מהיר, ארעי). | **החלטת היברידי** |
| **גרנולריות הסביבה** | **session אחד משותף per board-run** (working tree יחיד); כתיבות/git **מסודרלות דרך workspace mutex**; עבודה שלא נוגעת ב-repo רצה במקביל. | **mordechay + mutex של eli** |
| **פעולות רגישות** | מגודרות ב-**שער-אישור חובה** דרך **approval proxy** על כלי MCP רגישים; מתבצעות תחת זהות התפקיד רק לאחר אישור. OBO נדחה. | eli (מותאם) |
| **GitHub** | **GitHub App per-board** + **PR-only** + `AllowedRepos` per-role. flow חיבור per-board קונקרטי ממרדכי. | **flow של מרדכי + אבטחה של eli** |
| **אימות חיצוני** | Azure → role MI + RBAC מצומצם. לא-Azure (git App token / db / SaaS) → **סודות Key Vault per-role**, נשלפים **JIT**, מוזרקים per-command, **נמחקים** אחרי, לא נשמרים לעולם. | eli |

## 5 המתחים הקיימים שיש לפתור

- **T1 — ה-edges/לולאות לא נשמרים** (רק localStorage בדפדפן ב-[board-context.tsx](../../projects/repos/TectikaAgentEnvironment/src/web/tectika-board/src/lib/board-context.tsx)). לטופולוגיית ה-DAG/QA אין אמת בבקאנד. **לתקן ראשון (שלב 0).**
- **T2 — התזמור per-task ולא per-board** ([RunsController](../../projects/repos/TectikaAgentEnvironment/src/api/TectikaAgents.Api/Controllers/RunsController.cs)). ריצת גרף משתרעת על משימות רבות → צריך ריצה ברמת הלוח.
- **T3 — גרסאות הארטיפקטים אינן בטוחות-מרוץ** ([InvokeAgentActivity.cs](../../projects/repos/TectikaAgentEnvironment/src/workflows/Activities/InvokeAgentActivity.cs): read-max-then-write, בלי ETag). הרצה מקבילית שוברת את זה.
- **T4 — שני runners חופפים** ([WorkflowAgentRunner.cs](../../projects/repos/TectikaAgentEnvironment/src/workflows/Services/WorkflowAgentRunner.cs) + [FoundryAgentService.cs](../../projects/repos/TectikaAgentEnvironment/src/api/TectikaAgents.Api/Services/FoundryAgentService.cs)) → מאחדים ל-`IAgentRuntime` יחיד.
- **T5 — אילוץ ה-replay של Durable** — כל IO מול Foundry/Cosmos/Service Bus נשאר ב-Activities; לולאת הסוכן היא Activity יחידה שמחזירה שליטה ל-orchestrator רק בשערי אישור.

## ארכיטקטורת היעד

```
Board (Goal + MasterPlan + Repo)
   │
   ▼
BoardGraphOrchestrator ── מפעיל ──► SandboxService ► ACA dynamic session (per board-run)
   │  fan-out/fan-in מעל TaskEdges                    └─ מריץ את src/agent-container/ MCP server
   │  Task.WhenAll(ready nodes)                           (כלי git/file/run_command/create_pr)
   ▼
TaskGraphNodeOrchestrator (per task: סבב סוכן, QA verdict, לולאה, אישור)
   │
   ▼
ContextManager (project brief + TaskBrief + upstream + retrieval, מתוקצב)
   │
   ▼
FoundryAgentRuntime (IAgentRuntime) ── thread קבוע per-task, לולאת-כלים, streaming
   │   כלים = MCP(צד-שרת, ב-session) + retrieve_artifact(function)
   ▼
PermissionGate ─ רגיש? ─► approval proxy ─► WriteApproval + WaitForExternalEvent
                                                 └─ אושר → ResumeToolCallActivity → MCP /execute-approved (זהות תפקיד, JIT)
   │  פולט agent_thinking / tool_call SSE + AuditEntry לכל קריאה
   ▼
git push / create_pr (PR-only) · azure_deploy · db_execute · http  — הכל תחת workspace mutex
```

---

## רכיבים

### 1. Runtime של Foundry (`IAgentRuntime`)
- **`IAgentRuntime`** חדש (`src/core/.../Interfaces/`): `EnsureAgentAsync(role)`, `EnsureThreadAsync(task)`, `RunTurnAsync(req)`. `AgentRunOutcome` = `{ status: Completed|RequiresApproval|Failed|BudgetExceeded, artifact, tokenUsage, briefUpdate, pendingToolCall? }`.
- **`src/workflows/Services/FoundryAgentRuntime.cs`** חדש (מחליף את `WorkflowAgentRunner.cs`; מוחק את `FoundryAgentService.cs`) בעזרת `Azure.AI.Agents.Persistent` (`PersistentAgentsClient`, `PersistentAgentThread`, `ThreadRun`, `RequiredAction`/`SubmitToolOutputsAction`, `StreamingUpdate`).
- ממפה `SystemPrompt`→instructions, `ModelOverride ?? DefaultModel`→model, כלי התפקיד + **endpoint ה-MCP של ה-session** → tool defs. שומר agent id ל-`AgentRole.FoundryAgentId`, thread id ל-`AgentTask.FoundryThreadId` (נשמר באיטרציות QA).
- Streams: טקסט→`agent_thinking`, עדכוני כלים→`tool_call` (שני סוגי `AgentEvent` שקיימים אך לעולם לא נפלטים היום). `AgentRolesController.Upsert` יוצר/מעדכן את סוכן ה-Foundry (מרדכי).

### 2. סביבת הרצה — MCP server על ACA dynamic session (היברידי)
- **`src/agent-container/`** (חדש, ממרדכי): **MCP server** ב-Node/TS (MCP SDK) + `Dockerfile`. ה-image נושא `git`, `az` CLI, runtimes לבנייה, לקוחות db. בעליית ה-session: `git clone --depth=1` של ה-repo של הלוח ל-workspace. כלים (קונקרטיים):

  | כלי | רגיש? |
  |---|---|
  | `read_file`, `list_files`, `git_status` | לא |
  | `run_command(cmd,cwd)` (tests/build) | לא (קריאה/בנייה); כתיבות → mutex |
  | `write_file`, `git_commit`, `git_create_branch` | לא (מקומי; מסודרל ב-mutex) |
  | **`git_push`**, **`create_pr`** | **כן → approval proxy** |
  | `azure_whatif`, `db_query`, `http GET` | לא |
  | **`azure_deploy`/`azure_resource_action`**, **`db_execute`**, **`http` מְשַׁנֶּה** | **כן → approval proxy** |

- **`SandboxService`** (`src/workflows/Services/`) מנהל את מחזור החיים של ה-ACA dynamic session (custom-container) **per board-run** — מכליל את `StartDevContainerActivity`/`StopDevContainerActivity` של מרדכי. שומר `McpEndpointUrl` + מזהה session על ה-board-run; הורס בסיום/כשל/idle. **Egress ב-allowlist** per board/role.
- **הערת היתכנות (סיכון #1):** לאמת ש-ACA dynamic session יכול לארח MCP server מאזין שנגיש ל-Foundry לאורך הריצה. **Fallback = ACI per-run בדיוק כמו אצל מרדכי** — כך שהנתיב מנוטרל-סיכון.

### 3. approval proxy לכלים רגישים (החלק הקשה)
Foundry קורא לכלי MCP ישירות, ולכן כדי לאכוף אישור אנושי באמצע-לולאה בלי לחסום קריאת-כלי ל-48 שעות:
- כלים רגישים (`git_push`, `create_pr`, `azure_deploy`, `db_execute`, `http` מְשַׁנֶּה) **לא מתבצעים בקריאה**. ה-MCP server רושם את הפעולה המוצעת (branch, diff, יעד) ומחזיר `PENDING_APPROVAL:{actionId}` לסוכן, ומסמן ל-API שלנו (`approval_required`).
- `TaskGraphNodeOrchestrator` מריץ את השער **הקיים** (`WriteApprovalActivity` + `WaitForExternalEvent`, אירוע `approval-tool-{actionId}`), בשימוש חוזר ב-`ApprovalsController.Respond` + `HttpTrigger.RaiseApprovalEvent` + SSE ללא שינוי.
- ב-**אישור** → **`ResumeToolCallActivity`** חדש קורא ל-`/execute-approved` המאומת של ה-MCP server עם ה-action id + **זהות התפקיד הנשלפת JIT** (GitHub App installation token / role MI / KV secret), מבצע, ומחזיר את התוצאה לסבב סוכן חדש (אותו thread). ב-**דחייה** → הסוכן מקבל "DENIED, תכנן מחדש."
- **`PermissionGate`** (`src/workflows/Safety/`) סוף-סוף **אוכף** את `AgentRole.Permissions` + `IdentityConfig`: מסווג `Allowed | NeedsApproval | Denied` מול `RequiresApprovalFor`/`CanPushCode`/`CanDeploy` **וגם** ה-allowlists (`AllowedRepos`/`AllowedAzureScopes`/`AllowedHosts`); מחוץ ל-allowlist = `Denied`.

### 4. Context Manager (+ TaskBrief)
- **`src/workflows/Services/ContextManager.cs`** מחליף את `BuildMessages()`. תחת `Foundry:MaxInputTokens` מרכיב: **(א)** project brief מ-`Board.Goal` + `Board.MasterPlan` חדשים; **(ב)** לוג ה-**`TaskBrief`** של המשימה (מרדכי); **(ג)** ארטיפקטים ישירים מלמעלה (`GetUpstreamArtifactsAsync`); **(ד)** כלי `retrieve_artifact` לפי דרישה (שאילתת keyword פשוטה ב-Cosmos; embeddings בהמשך). ארטיפקטים גדולים מדי מסוכמים פעם אחת → נשמרים ב-`Artifact.Summary` חדש.
- **לולאת TaskBrief (מרדכי):** הסוכן מסיים כל סבב ב-`## Brief Update` של שורה אחת; `InvokeAgentActivity` פורס ומוסיף `[role, runId[:6], step]: …` ל-`AgentTask.TaskBrief`. מתנקה כשהמשימה → Done.

### 5. Orchestrator של הגרף (DAG + לולאות QA)
- **`TaskEdge`** חדש + container `taskEdges` ב-Cosmos (PK `boardId`): `{FromTaskId, ToTaskId, EdgeKind(Dependency|QaFeedback), Condition, MaxIterations=3}` — נשמר ע"י `connectTasks` + ה-UI של הקנבס (**מתקן את T1**); ה-`Upstream/DownstreamTaskIds` הקדמיים ממשיכים לשקף לתצוגות הלוח.
- **`BoardGraphOrchestrator.cs`** חדש: מפעיל את ה-session (SandboxService) → טוען משימות+edges → מחשב **ready set** (כל הורי `Dependency` ב-`Completed`) → `Task.WhenAll(CallSubOrchestratorAsync(TaskGraphNodeOrchestrator))` → join → לולאה עד סיום → עוצר session (ב-`finally`). תופס גרסאות ארטיפקט-הורה ב-dispatch (**join determinism**).
- **`TaskGraphNodeOrchestrator`** מכליל את [TaskPipelineOrchestrator.cs](../../projects/repos/TectikaAgentEnvironment/src/workflows/Orchestrators/TaskPipelineOrchestrator.cs) של היום. צמתי QA פולטים `QaVerdict` מובנה. ב-**כשל + מתחת לתקרה** → dispatch מחדש של צומת ה-dev **בשימוש חוזר ב-thread** עם משוב ה-QA; **בתקרה** → הסלמה ל-`role.EscalateTo` דרך flow האישור/המתנה.

### 6. סנכרון ובטיחות
- **Workspace mutex:** Durable Entity לפי session ה-board-run מסדרל כל קריאת כלי מְשַׁנָּה קובץ/git על העץ המשותף; קריאה/חשיבה/HTTP/DB-read רצים במקביל. (מפשר בין "DAG מקבילי" ל-"קונטיינר משותף אחד".)
- **מרוץ ארטיפקטים (T3):** מזהים דטרמיניסטיים `{taskId}:{runId}:{iteration}` (כפילות = 409) + `IfMatchEtag` על `currentArtifactId` של המשימה.
- **סודות:** JIT תחת role MI, מוזרקים כ-env vars ארעיים לפקודה אחת, נמחקים אחרי; image/ארטיפקטים/לוגים נקיים מסודות.
- **תקציבים:** `WorkflowRun.MaxTokens`/`MaxCostUsd` (בדיקת חריגה ב-`UpdateRunStatusActivity`, שכבר מצבר סכומים) + `maxCompletionTokens` על ריצת Foundry; תקרת לולאה = `TaskEdge.MaxIterations`.
- **Audit:** כל קריאת כלי → `AuditEntry` דרך `WriteAuditActivity`/`AppendAuditAsync`.

### 7. חיבור GitHub App per-board (flow של מרדכי + אבטחה של eli)
- **`Board`** + `RepoUrl`, `RepoOwner`, `RepoName`, `DefaultBranch`(="main"), `GitHubInstallationId` / `GitHubTokenSecretName`.
- **`GitHubController`** (חדש): `GET …/github/connect` (URL התקנת App), `…/github/callback`, `…/github/status`, `DELETE …/github` (ניתוק → ניקוי KV); מחיקת לוח → ניקוי.
- כלי git משתמשים ב-**installation token קצר-מועד של ה-App** הנשלף JIT (private key של ה-App ב-Key Vault), מצומצם ל-`AllowedRepos`; **PR-only** — לעולם לא push לענפים מוגנים; `create_pr` הוא הפעולה המגודרת.
- **Frontend** ([app/boards/page.tsx](../../projects/repos/TectikaAgentEnvironment/src/web/tectika-board/src/app/boards/page.tsx)): מחליפים את `window.prompt` במודל דו-שלבי (Name/Description → "Connect GitHub" אופציונלי); כרטיס הלוח מציג badge `org/repo ✓`.

### 8. סיכום מודל-נתונים וקונפיג
- **מודלים/containers חדשים:** `TaskEdge`(+`taskEdges`), `IAgentRuntime`/`AgentRunRequest`/`AgentRunOutcome`/`PendingToolCall`, `QaVerdict`, `BoardRun`, entity ל-workspace-mutex.
- **שונו (שימוש חוזר בשדות):** `AgentRole` (הפעלת `FoundryAgentId`/`Tools`/`McpServers`; +`IdentityConfig`{MI id, KV refs, `AllowedRepos`/`AllowedAzureScopes`/`AllowedHosts`}); `AgentTask` (+`FoundryThreadId`, +`TaskBrief`); `Board` (+`Goal`,`MasterPlan`,שדות repo); `Artifact` (+`Summary`,ETag); `WorkflowRun`/`BoardRun` (+`SessionId`,`McpEndpointUrl`,`MaxTokens`,`MaxCostUsd`,`BoardRunId`).
- **קונפיג:** `FoundrySettings`(+`MaxInputTokens`, שימוש ב-`ProjectName`), חדשים `McpSettings`, `SandboxSettings`(ACA pool/image/timeouts/egress), `GitHub`(App id + secret של private-key), `Azure`(subscription/RG/location), `BudgetSettings`, ה-`KeyVaultSettings` הקיים.
- **Mock parity:** `InMemoryCosmosDbService` + `MockAgentRuntime` + `MockSandbox`/`MockMcp` + GitHub מדומה → כל לוגיקת הגרף/אישור/לולאה/תקציב נבדקת **בלי Azure**.

---

## סדר מימוש (כל שלב נבדק ב-mock)

| שלב | תוצר | מקור |
|---|---|---|
| **0** | שמירת `TaskEdge` (מודל, container, `connectTasks` + הגירת קנבס מ-localStorage) — משחרר את ה-DAG | eli (T1) |
| **1** | `IAgentRuntime` + `FoundryAgentRuntime` סבב-יחיד + threads per-task; איחוד שני ה-runners; `AgentRolesController` יוצר/מעדכן agent | שתיהן |
| **2** | **TaskBrief** (מהיר, עצמאי) + Context Manager (brief + upstream; retrieval/summarize תת-שלב); `Board.Goal`/`MasterPlan` | מרדכי + eli |
| **3** | **חיבור GitHub App per-board** (controller, שדות לוח, מודל דו-שלבי, badge, KV) | מרדכי + eli |
| **4** | **ACA session + `src/agent-container/` MCP server** per board-run; `SandboxService` start/stop; כלים **לא-רגישים** מקצה לקצה | מרדכי על ACA |
| **5** | `PermissionGate` + **approval proxy** לכלים רגישים (`git_push`/`create_pr`/`azure_deploy`/`db_execute`/http מְשַׁנֶּה) — אישור באמצע-לולאה | eli (מותאם) |
| **6** | `BoardGraphOrchestrator` fan-out/fan-in + board-run (T2) + **workspace mutex** | eli |
| **7** | לולאות QA: `QaVerdict`, max-iter, הסלמה לאדם | eli |
| **8** | סנכרון ותקציבים: artifact ETag (T3), תקרות token/cost | eli |

## בדיקה

- **Mock (כל שלב):** API + Workflows עם `MockDatabase:Enabled=true` + `MockAgentRuntime` + `MockMcp`. הרצת DAG יהלום (A→{B,C}→D) + לולאת dev↔QA: לוודא ש-B/C מקבילים, D מצרף את שניהם, לולאת QA מתאתרת ואז מסלימה בתקרה, כלי רגיש **עוצר באישור** וממשיך באישור / מתכנן מחדש בדחייה, חריגת תקציב טוקנים עוצרת את הריצה, וה-**workspace mutex** מסדרל שתי כתיבות מקביליות.
- **TaskBrief:** הרצת משימה פעמיים → `TaskBrief` מצטבר; משימה → Done → `TaskBrief` מתנקה.
- **GitHub + container (E2E של מרדכי):** חיבור לוח ל-repo; הרצה → ACA session/ACI נוצר (ונהרס); "קרא README" (read_file דרך MCP), "כתוב+הרץ test" (write+run_command), "פתח PR" → PR קיים; repo מחוץ ל-allowlist = `Denied`; אין סוד באף ארטיפקט/לוג/session.
- **SSE:** `GET /api/runs/{runId}/stream` מראה `agent_thinking`/`tool_call`/`approval_required`/`step_completed` זורמים בפועל (הפעלה לפי [running-agentboard-for-visual-qa](../../.claude/projects/-home-elimeshi-projects-repos-TectikaAgentEnvironment/memory/running-agentboard-for-visual-qa.md)).
- **Azure smoke (אחרי mock):** פרויקט Foundry אמיתי + flow GitHub PR-only אמיתי אחד עם KV App token + Azure what-if אחד תחת role MI.

## סיכונים עיקריים

1. **הארחת MCP על ACA** — האם ACA dynamic session יכול לארח MCP server נגיש לאורך הריצה? לאמת מוקדם; **fallback = ACI per-run (המקור של מרדכי)**.
2. **סחף SDK בטא** — `Azure.AI.Agents.Persistent`/`Projects` הם בטא; לקבע גרסאות + לבודד מאחורי `IAgentRuntime`.
3. **תקינות ה-approval proxy** — עצירה/חידוש של פעולה רגישה out-of-band בעוד thread ה-Foundry נשאר קוהרנטי; לאמת את נתיב ה-resume-into-thread.
4. **מקבילי + עץ משותף** — workspace mutex + artifact ETag חייבים לנחות לפני fan-out אמיתי.
5. **הגירת edges בפרונט** — העברת edges של לולאה/תווית מ-localStorage ל-`TaskEdge` דורשת הגירת לוחות חד-פעמית.
6. **משמעת סודות/egress** — הזרקת JIT + מחיקה + egress allowlist חייבים להיות אטומים; דליפה כאן היא התרחיש הגרוע.
7. **מחזור חיים ועלות ACA/ACI** — הריסה מובטחת + idle timeouts; sessions יתומים הם סיכון עלות/אבטחה.
8. **הקמת GitHub App** — התקנה בצד הארגון + installation tokens מצומצמים; רוטציה של private key של ה-App דרך Key Vault.
