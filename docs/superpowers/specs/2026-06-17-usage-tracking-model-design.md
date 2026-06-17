# Usage & Cost Tracking Model — Design

**Date:** 2026-06-17
**Status:** Approved (design); pending implementation plan
**Branch:** `feat/usage-tracking-model`

## 1. Purpose & scope

Build a complete, accurate, multi-model usage- and cost-tracking model for the Tectika agent
environment, plus the UI to surface it. Replace the current placeholder cost logic (a single
blended token rate) and the misleading per-run-only token display with a durable, model-aware
system that answers three questions reliably:

1. **What has this project cost, in total?** — never lost when chats are cleared, tasks deleted, or
   runs re-run.
2. **What is each model actually costing?** — input/output/cached priced separately, per provider +
   model, so an aggregate token count is never mistaken for cost.
3. **What has this task used — this conversation, and over its whole life?** — both views available.

**In scope:** accurate token capture (incl. cached + reasoning), a provider-agnostic pricing
catalog, an append-only usage ledger, materialized rollups (project / board / task), full run
lifecycle edge-case handling, API endpoints, UI (table columns, item panel, dashboards/analytics),
backfill of existing data, mock-seeder update, and infra container definitions.

**Out of scope (this round):** budgets, spend alerts, and hard caps. The design leaves a clean path
to add them later (rollups already hold the running totals they'd check against). External
billing-grade metering pipelines (e.g. OpenMeter) are explicitly not used.

## 2. Current state (findings)

- **Token capture path is correct but shallow.** `FoundryAgentRuntime` reads `InputTokens` /
  `OutputTokens` from the Foundry response ([FoundryAgentRuntime.cs:188,245](../../../src/agentruntime/FoundryAgentRuntime.cs));
  `AgentToolLoop` accumulates them per round ([AgentToolLoop.cs:71-73](../../../src/agentruntime/AgentToolLoop.cs#L71-L73)).
  It ignores cached tokens and reasoning tokens.
- **Cost is a flat blended rate** — `run.TotalTokens * 0.00003m`
  ([UpdateRunStatusActivity.cs:57](../../../src/workflows/Activities/UpdateRunStatusActivity.cs#L57)).
  Not model-aware; ignores that output tokens cost far more than input.
- **No model attribution on usage.** `TokenUsage` is `{input, output, total}` — per-model cost is
  impossible to compute today.
- **Table tokens = last run only.** A task points to one `WorkflowRunId`; the column reads
  `run.totalTokens` ([columns.ts:92-93](../../../src/web/tectika-board/src/lib/columns.ts#L92-L93)).
  Re-running repoints it, so prior runs vanish from the table. Not cumulative per task, and not per
  project.
- **No durable project cost.** The dashboards page sums by querying all runs on the fly — fragile;
  orphaned/deleted runs are lost.
- **What the table shows today is seeded mock data** — the wiring is real, but
  [MockDataSeeder.cs:288-320](../../../src/api/TectikaAgents.Api/Services/MockData/MockDataSeeder.cs#L288-L320)
  hardcodes `TotalTokens` / `EstimatedCostUsd`.
- **`/clear`** ([ChatService.cs:137](../../../src/api/TectikaAgents.Api/Services/ChatService.cs#L137))
  nulls the thread + brief and stamps `ChatClearedAt`; it does not touch usage.
- **`/compact`** runs an LLM summarization whose tokens are currently **untracked**.

Conclusion: the capture path is accurate per round, but the cost model, attribution, durability, and
aggregation are not. This is a model redesign, not a bug fix.

## 3. Architecture (chosen: append-only ledger + materialized rollups)

Every agent round writes one immutable usage event carrying full attribution and a frozen cost
snapshot. Cheap-to-read rollup documents (project / board / task) are incremented as events land.
Reads (table, panels, dashboards) hit rollups, never scan events.

Rejected alternatives: **compute-on-read from runs** (project cost dies on delete/orphan; no
session view; expensive cross-partition scans), and **external metering service** (heavy dependency,
conflicts with the self-contained idempotent-infra rule).

### 3.1 Token shape

`TokenUsage` (Core model + TS mirror) becomes:

```
input        // total prompt tokens (INCLUDES the cached portion)
cachedInput  // subset of input served from cache — billed at the cached rate
output       // total completion tokens (INCLUDES reasoning)
reasoning    // subset of output spent reasoning — informational only
total => input + output
```

`FoundryAgentRuntime` additionally parses `prompt_tokens_details.cached_tokens` and
`completion_tokens_details.reasoning_tokens`, defaulting to 0 when absent. `AgentToolLoop`
accumulates all four fields across rounds.

**Cost formula:**
`cost = (input − cachedInput)·inputRate + cachedInput·cachedRate + output·outputRate`
Reasoning is **not** added separately — it is already counted inside `output`.

### 3.2 Pricing catalog

A versioned JSON catalog checked into `Core` (git is the source of truth — satisfies the
infra-idempotency rule). Each entry:

```
{ provider, model, modelVersion?, inputPerMillion, cachedInputPerMillion,
  outputPerMillion, currency, effectiveFrom }
```

A shared `CostCalculator` service resolves the rate for `(provider, model, at-timestamp)` using the
newest entry whose `effectiveFrom <= timestamp`, and computes cost. Effective-dated entries mean
adding a model or changing a price is pure data; because each event freezes the rate it used,
historical costs never silently change. The catalog carries a `catalogVersion` recorded on each
event.

Initial catalog seeds Azure Foundry `gpt-4o` with correct split input/output (and cached) rates;
other providers/models are added as data with no code change.

### 3.3 Usage ledger — `usageEvents` container

Partition key `/taskId`. One immutable record per **round** (see §4 for why round, not step):

```
id            = "{runId}:{step}:{invocationId}:{round}"   // see §4 — invocation-keyed, idempotent
tenantId, boardId, taskId, runId, step, round
agentRoleId, agentRoleName                                // role = "system:compaction" for compaction
provider, model, modelVersion                             // attribution, surfaced up from the runtime
sessionId                                                 // task session; resets on run-start / clear / compact
usage         : TokenUsage                                // the 4-field shape
pricing       : { catalogVersion, inputPerM, cachedPerM, outputPerM, currency }
costUsd                                                   // frozen snapshot (0 when pricingMissing)
pricingMissing: bool                                      // true when no catalog entry matched
timestamp
```

### 3.4 Rollups — `usageRollups` container

Partition key `/tenantId`. Three scopes, all under the tenant partition so a project + its boards +
tasks read from one partition:

```
id = "project:{tenantId}" | "board:{boardId}" | "task:{taskId}"
scope, scopeId, tenantId
lifetime        : { tokens:{input,cachedInput,output,reasoning,total}, costUsd, eventCount }
perModel        : { "azure-foundry/gpt-4o": { tokens, costUsd, eventCount }, … }
currentSession? : { sessionId, since, tokens, costUsd, eventCount }   // task scope ONLY
updatedAt
```

`perModel` is what keeps the aggregate honest: total cost is always decomposable by model.

## 4. Write path, idempotency & concurrency

When a round returns inside `InvokeAgentActivity`, the workflow:

1. Computes cost via `CostCalculator` and writes the `usageEvent` with `CreateItemAsync`.
2. On **`409 Conflict`** (same response written twice — e.g. a write-level redelivery) → the event
   already exists → **skip** the rollup increments. Prevents double-counting a single LLM response.
3. Otherwise increments the project, board, and task rollups (and the task's `currentSession`).

**Event id is invocation-keyed: `{runId}:{step}:{invocationId}:{round}`** where `invocationId` is the
activity's `FunctionContext.InvocationId`. Rationale:

- `round` → a stopped/failed run keeps every round that already returned; the loss window is a single
  in-flight round whose response had not returned yet (nothing billable lost in practice).
- `invocationId` → when Durable Functions **retries a failed activity**, it genuinely re-calls the
  LLM. That is *real duplicate spend* and must be counted; a new invocation id ensures it is. A
  redelivery of the *same* response collides on the id and is deduped via the `409` skip. Model
  attribution is unaffected because the model is constant within a step.

**Concurrency:** project and board rollups are hot documents updated by many concurrent step
completions (downstream cascades, parallel runs). Naive read-modify-write would lose increments.
Rollup numeric fields are updated with **Cosmos atomic `Patch` increments**; adding a new `perModel`
key (a path that may not yet exist) uses an **ETag-guarded retry loop** (read → patch-add the key →
retry on precondition failure). This makes increments race-safe.

Cost computation lives in `workflows`; the runtime only *reports* tokens + model attribution and
stays pricing-free (clean separation).

## 5. Run lifecycle edge cases

A new `sessionId` is assigned on **run start, `/clear`, and `/compact`** — not on pause/resume or
Durable retry.

| Event | Usage behavior |
|---|---|
| **Stop / Cancel** (`StopAsync`) | Completed rounds already in the ledger → real spend stays in all rollups, **no decrement**. Run → `Cancelled`. At most one in-flight round (response not yet returned) is uncaptured — documented, negligible. |
| **Compact** (`/compact`) | The summarization LLM call (currently untracked) now writes a usage event, role `system:compaction`, model = task's model, under the **current** session; then the session rolls over (new `sessionId`). Cost lands in lifetime + project/board. |
| **Clear** (`/clear`) | Bump `sessionId`; reset the task rollup's `currentSession` bucket only. Lifetime / perModel / project / board untouched. |
| **Retry — QA loop / downstream re-run** | A fresh run = fresh `sessionId`. `currentSession` shows the new run; `lifetime` accumulates across all runs. No decrement of prior runs. |
| **Retry — Durable activity re-execution** | Counted as real spend (new `invocationId`) — the LLM was really called again. |
| **Pause / resume** (`PausedApproval`, `AwaitingInteraction`) | Same run + session; resume keeps accruing. Idempotent ids prevent replay double-count. |
| **Failed run** | Completed rounds' spend stays (tokens really consumed). Run → `Failed`. |
| **MaxRoundsHit** | Nothing special — all rounds captured normally. |
| **Delete task / board** | Project/board `lifetime` + `perModel` rollups are **never decremented** (real money already spent). The deleted entity's own rollup + events are removed with it. |
| **Concurrent runs** | Atomic `Patch` increments + ETag-retry for new `perModel` keys (see §4). |
| **No pricing entry for a model** | Usage still recorded; `costUsd = 0`, `pricingMissing = true`, warning logged. UI shows "tokens tracked, cost unavailable" instead of a wrong number. |
| **Provider omits usage** (errors/streaming) | Defaults to 0; round still completes — never fails the run. |

## 6. API surface

New `UsageController`:

- `GET /api/usage/project` → tenant rollup (lifetime + perModel).
- `GET /api/usage/board/{boardId}` → board rollup.
- `GET /api/usage/task/{taskId}` → `{ currentSession, lifetime, perModel }`.
- `GET /api/usage/task/{taskId}/events` → paged event history (drill-down).

## 7. UI

- **Table `tokens` / `cost` columns** → read the task rollup's `currentSession` (matches "current
  conversation"), replacing last-run-only. Cost column formats per-currency.
- **Live chat (`LiveEdge`)** → keeps summing in-flight stream events; reconciles to `currentSession`
  on completion.
- **`ItemPanel` usage panel** → a **Session ⇄ Lifetime** toggle plus a per-model breakdown table
  (tokens + cost per model). This is the "inspect the whole history of this task" view.
- **Dashboards / Analytics** → read project & board rollups (replacing the fragile on-the-fly
  `runs.reduce` sum); add a **per-model cost breakdown** as the headline anti-"misleading aggregate"
  view.
- States: `pricingMissing` shows "cost unavailable"; zero-usage shows an empty state, not `$0.00`
  noise where misleading.

## 8. Data integrity, migration & infra

- **Backfill utility** (one-time): synthesize task / board / project rollups from existing
  `WorkflowRun.TotalTokens`, attributed to the default model, so pre-existing data does not read as
  zero. Best-effort; flagged as backfilled.
- **`WorkflowRun.TotalTokens` / `EstimatedCostUsd`** remain updated for backward-compat, but the
  ledger + rollups are the authoritative source.
- **Mock seeder** → seed realistic `usageEvents` + rollups across **multiple** models so the
  per-model UI is demonstrable in mock mode.
- **Infra** → add `usageEvents` (`/taskId`) and `usageRollups` (`/tenantId`) containers to the
  `infra/` definitions **and** `EnsureInfrastructureAsync` (infra must stay idempotent and
  recreatable; new Cosmos containers must be created explicitly, not relied upon to auto-create).

## 9. Component boundaries

- **`Core`** — `TokenUsage` (extended), `UsageEvent`, `UsageRollup` models; the pricing catalog
  (JSON + typed loader); `CostCalculator` (pure, no I/O).
- **`agentruntime`** — extended usage parsing (cached/reasoning) + model attribution on the run
  outcome. No pricing knowledge.
- **`workflows`** — writes usage events + increments rollups (idempotent, concurrency-safe); compaction
  usage capture.
- **`api`** — `UsageController`; rollup read services; backfill; seeder; infra container creation.
- **`web`** — usage types (mirror), table columns, item-panel usage panel, dashboards/analytics
  per-model views.

## 10. Open questions

None outstanding. Budgets/alerts/caps deferred by decision; revisit as a follow-up spec.
