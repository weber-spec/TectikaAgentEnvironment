# Migrate agent runtime to the new Foundry agent model — Design

> Status: **approved pending spec review** (2026-06-10). Branch: `feat/foundry-new-agent-migration`.
> Supersedes the legacy-assistants approach from [2026-06-09-phase1-foundry-agents-design.md](2026-06-09-phase1-foundry-agents-design.md).

## Problem

Phase 1 created agents via `Azure.AI.Agents.Persistent` (`PersistentAgentsClient`), which targets
the **legacy Assistants API** — agents get `asst_…` ids and live under `{project}/assistants`. These
do **not** appear in the new Foundry **project Agents** view (verified live: a created `asst_…` agent
was retrievable only via `/assistants`, not the new `/agents` surface). We standardize on the **new
Foundry** everywhere, where an agent is identified by **name** (no `asst_` GUID).

## New Foundry contract (verified live against `proj-agentteam`)

- **Create/update agent:** `POST {project}/agents?api-version=v1`
  `{ "name": "<stable id>", "definition": { "kind": "prompt", "model": "<deployment>", "instructions": "<prompt>", "description": "<optional>" } }`
  → returns `{ object:"agent", id:"<name>", name:"<name>", versions:{ latest:{ id:"<name>:1", version:"1", definition, status:"active" } } }`. **`id == name`**; agents are **versioned**; each gets its own managed identity. "No GUID `AgentID` anymore."
- **Get / list / delete:** `GET {project}/agents/{name}?api-version=v1`, `GET {project}/agents?api-version=v1`, `DELETE {project}/agents/{name}?api-version=v1`.
- **Conversation (per-task history):** `POST {project}/conversations?api-version=v1` → `{ id:"conv_…" }`.
- **Run (Responses API):** `POST {project}/openai/v1/responses`
  `{ "input": "<user text>", "agent_reference": { "name":"<agent name>", "type":"agent_reference" }, "conversation": "conv_…" }`
  → `{ status:"completed", output:[ { type:"message", content:[ { type:"output_text", text:"…" } ] } ], usage:{ input_tokens, output_tokens, total_tokens } }`. Multi-turn also supports `previous_response_id`.
- **Auth:** bearer token, scope `https://ai.azure.com/.default`, via `DefaultAzureCredential` (the container app's user-assigned MI). RBAC = **Foundry User** (`53ca6127-…`, `Microsoft.CognitiveServices/*`), already granted.

## Agent identity model (key decision)

The Foundry agent **name = a stable, app-generated token**, created once and stored in
`AgentRole.FoundryAgentId`. The **user-assigned display name is UI-only** (`AgentRole.DisplayName`)
and is mirrored into the agent's `description` for portal readability — it is **not** the identity.

- **First sync** (empty `FoundryAgentId`): generate a name → create the agent → persist the name.
- **Rename** (only `DisplayName` changes): the Foundry agent name/identity is **never** touched — no
  delete/recreate. (Change-detection hashes only prompt+model, so a rename doesn't bump the version.)
- **Edit prompt/model:** POST a new **version** of the same agent name.

Generated name format: `tk-{namePrefix}-{8 random lowercase hex}` (e.g. `tk-agentteam-1a2b3c4d`),
constrained to Foundry's agent-name rules (lowercase alphanumeric + hyphens; exact length/regex
confirmed during implementation). Random suffix avoids cross-tenant/project collisions.

## Per-task context (Approach A — conversation per task)

A **conversation per task** preserves the "persistent thread per task" intent (history across
QA retries). `AgentTask.FoundryThreadId` now holds a `conv_…` id (semantics change; field reused).

## Components

### `src/agentruntime/FoundryAgentRuntime.cs` — rewrite to raw REST
- Replace the `PersistentAgentsClient` SDK usage with a typed `HttpClient` calling the endpoints
  above. Acquire the bearer token via `DefaultAzureCredential().GetToken(["https://ai.azure.com/.default"])`,
  cached/refreshed per the token's expiry. Base URL = `FoundrySettings.ProjectEndpoint`.
- **Remove** the `Azure.AI.Agents.Persistent` package reference from the csproj.
- `EnsureAgentAsync(role)`:
  - If `role.FoundryAgentId` empty → generate name, `POST /agents` with the prompt definition +
    `description = role.DisplayName`, set `role.FoundryAgentId = name`, `role.FoundryAgentHash = hash(prompt,model)`.
  - Else if `role.FoundryAgentHash != hash` → `POST /agents` for the same name (new version) with the
    updated definition; refresh hash. (Also refresh `description` best-effort.)
  - Catch/log exceptions → `AgentSyncResult(role.FoundryAgentId, Synced:false, error)` (no throw) — unchanged contract.
- `DeleteAgentAsync(name)` → `DELETE /agents/{name}` (no-op on null/404).
- `EnsureThreadAsync(task)` → if `task.FoundryThreadId` set, reuse; else `POST /conversations`, set + return `conv_…`.
- `RunTurnAsync(req)` → `POST /openai/v1/responses` with `input = req.UserMessage`,
  `agent_reference{ name = req.Role.FoundryAgentId }`, `conversation = req.ThreadId`. Map terminal
  `status`: `completed`→Completed; `incomplete` (token cap)→BudgetExceeded; `failed`/`cancelled`→Failed. Extract
  text from `output[]` message `output_text` items, `usage.input_tokens→Input`, `output_tokens→Output`,
  `completionId = response.id`. Fire `OnText` with the output (one event, as today).

### Unchanged
- `IAgentProvisioner` / `IAgentRuntime` / `AgentRunRequest` / `AgentRunOutcome` — same shapes; only
  `FoundryAgentId` **semantics** change (now = stable agent name).
- `MockAgentRuntime` / `MockAgentProvisioner` — already name-like mock ids; keep as-is.
- `AgentRolesController` (eager provision on upsert/delete), `InvokeAgentActivity`, DI registration.

### Config / infra
- Uses existing `Foundry__ProjectEndpoint`. No new env/secrets. No bicep change (RBAC already = Foundry User).

## Data migration
None. All live `AgentRole.FoundryAgentId` are currently null (legacy attempts never persisted an id;
the one test agent was deleted). Re-saving a role creates a new-model agent cleanly.

## Out of scope
Agent tools (web search / function / MCP), streaming responses, advanced conversation features
(forking, manual item management), and the workflows Function App deploy itself.

## Verification
- **Unit (mock):** existing tests stay green; the mock path is unchanged.
- **Azure smoke (live):** save a role in the Agents tab → it appears in the Foundry **project Agents**
  view named `tk-…`, `synced:true`, `FoundryAgentId` = that name; rename the role → Foundry agent
  unchanged (same name, no new agent); run a task → `/openai/v1/responses` returns output + real
  token usage; the artifact is produced.
