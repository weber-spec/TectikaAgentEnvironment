# Custom Tool Connections for Agents (MCP) — Design

**Status:** approved (design); ready for implementation plan
**Date:** 2026-06-29
**Branch:** `feat/mcp-custom-tools`

## Problem

Agents today have a fixed, code-defined toolset ([`TectikaToolSchema.cs`](../../../src/agentruntime/TectikaToolSchema.cs)): board-explore, control, GitHub-read, and workspace/file tools. There is no way for a user to give an agent access to an external SaaS — Gmail, Slack, Canva, Notion, Linear, etc. We want users to **connect custom tools** so agents can both **read from and act on** those services.

## Decisions (locked with user)

- **Capability = read + act (write).** Agents can both fetch data and take outbound actions (post to Slack, send email, etc.).
- **Identity = per-board shared connection.** A service is connected once per board (like the existing `Board.GitHub` repo connection); all agents on that board share it. Actions run as that shared account/bot identity. OAuth / per-user on-behalf-of (OBO) is explicitly **deferred**.
- **Extensibility = hybrid: a curated catalog built on MCP.** The [Model Context Protocol](https://modelcontextprotocol.io) is the engine; we ship a vetted catalog of "verified" integrations and can add more cheaply.
- **Architecture = self-hosted MCP proxy.** We run the MCP **client** inside `agentruntime`, project integration tools onto the Foundry agent definition as flat functions, and dispatch their calls through a new executor on the existing `CanHandle` / `ExecuteAsync` seam — keeping our scrubbing, observability, enablement, and write-gating.
- **v1 transport/auth = remote HTTP + token auth.** `agentruntime` connects directly to remote/hosted MCP servers over Streamable HTTP/SSE. The per-board credential is an API key / bot token stored in Key Vault (the `PatSecretName` pattern). No process host. v1 catalog = token-auth services (Slack, Notion, Linear, GitHub, …). Self-hosting stdio servers, OAuth-only services (Gmail, Canva), per-user identity, and a managed-gateway source are out of scope for now.

## Load-bearing insight — role drives the tool surface, board supplies the credentials

`AgentRole` is **tenant-scoped** and its Foundry agent definition is **shared across every board** in the tenant; connections are **per-board**. We therefore cannot bake board-specific tools into the agent definition. We use the same split GitHub already uses:

- **The role decides the *tool surface*** — which integrations an agent is *allowed* to use → drives what's on the Foundry definition.
- **The board supplies the *credentials/endpoint*** at run time → decides whether a call actually works.

So if a role enables "slack", the agent definition always carries Slack's tools. At run time `slack__post_message` looks up *this board's* Slack connection; if the board has none, the tool returns a friendly *"Slack isn't connected to this board"* and the model moves on. This mirrors `github_read_file` being a definition-level tool while the repo + PAT come from `req.BoardGitHub` at run time.

Consequence: **the curated catalog pins each integration's tool schema** (we vet *and* fix the exposed tool list), rather than building the definition from a live per-board enumeration. Curation already chooses which servers are offered; it now also chooses which of their tools to expose (which keeps per-agent tool counts bounded).

---

## Component 1 — The curated catalog (`McpCatalog`)

A static, code-defined registry (analogous to the existing [`FoundryModelCatalog.cs`](../../../src/agentruntime/FoundryModelCatalog.cs)). One entry per integration:

- `Id` (e.g. `"slack"`), display metadata (name, description, icon hint, vendor).
- **Endpoint** — the remote MCP server URL (or a template).
- **Auth descriptor** — what the user pastes (e.g. *"Slack Bot Token (xoxb-…)"*), how it is sent as a header (scheme/header name), and a help link.
- **Pinned tool schema** — the list of tool defs we expose, each flagged **read** or **write**. Tool names are namespaced by integration id (`slack__post_message`) to avoid collisions with built-in tools and each other.
- A catalog `Version` constant (feeds the agent hash; bumping it republishes agents — same role as `TectikaToolSchema.Version`).

v1 ships a small set of token-auth integrations. Adding one later = a catalog entry (+ verifying the pinned tool schema against the live server during development).

## Component 2 — Per-board connection model

Add `mcpConnections: List<McpConnection>` to [`Board`](../../../src/core/TectikaAgents.Core/Models/Board.cs) (mirrors `Board.GitHub`). Each `McpConnection`:

- `connectionId` (guid), `catalogId` (`"slack"`), instance `displayName` (e.g. "Acme Slack").
- `secretName` — Key Vault secret holding the token, stored via `ISecretProvider.SetSecretAsync` (same pattern as `GitHubRepoConnection.PatSecretName`). Naming: `mcp-{boardId}-{connectionId}`.
- `status` (`Connected` / `Error` / `Disconnected`), `lastValidatedAt`, `createdBy`, `createdAt`.

**Lifecycle (API):**
- **Connect** — validate by opening an MCP client to the endpoint with the pasted token and listing tools (auth check); on success store the token in KV and persist the connection (`status = Connected`).
- **Validate / refresh** — re-run the auth check; update `status` / `lastValidatedAt`.
- **Disconnect** — delete the KV secret and remove the connection from the board.

Board connect/disconnect is **runtime-only**: it changes whether a tool works, not the agent definition, so it **never triggers an agent republish**.

## Component 3 — Per-agent enablement (wire the vestigial `McpServers`)

[`AgentRole.McpServers`](../../../src/core/TectikaAgents.Core/Models/AgentRole.cs) is currently stored and shown as a UI tag but never used. Repurpose it: it holds the **catalog ids a role may use** (e.g. `["slack","notion"]`). Enablement is **two levels per integration**:

- Enabling an integration grants its **read** tools.
- **Write** tools require an explicit per-integration *"allow write actions"* opt-in. Write tools are **omitted from the agent definition unless opted in** — the model cannot call what it cannot see, so the human granting the opt-in is the v1 approval gate.

Serialization: keep `McpServers` as the enabled list and carry the write opt-in alongside it (e.g. a parallel `mcpWriteEnabled: List<string>` of catalog ids, or upgrade `McpServers` entries to a small record `{ catalogId, allowWrite }`; exact shape decided in the plan, kept backward-compatible with the existing `List<string>`).

## Component 4 — Definition projection & republish

- Extend `TectikaToolSchema.ToFoundryToolsJson(...)` (or a sibling) to append, for each catalog id in `role.McpServers`, that integration's pinned namespaced tool defs — read tools always, write tools only when the role opted in.
- Extend `AgentInstructionsHash.Compute(...)` to include `role.McpServers` (+ write opt-ins) and `McpCatalog.Version`. So **editing a role's integrations** or **changing the catalog** republishes the agent; board connect/disconnect does not.

## Component 5 — Runtime dispatch (`McpToolExecutor`)

A new executor plugged into [`RoundExecutor.ExecuteOneRoundAsync`](../../../src/agentruntime/RoundExecutor.cs) via the same pattern as `IGitHubToolExecutor` / `WorkspaceToolExecutor`:

- `CanHandle(name)` — true for any namespaced catalog tool name.
- `ExecuteAsync(name, args, boardConnections, role, ct)`:
  1. Resolve the integration id from the tool name; find this board's connection for it.
  2. **No connection** → return a friendly structured error (*"slack isn't connected to this board"*).
  3. **Write tool but role not write-opted-in** → structured error (defensive; normally absent from the definition).
  4. Otherwise resolve the token from `ISecretProvider`, open an MCP client (official `ModelContextProtocol` .NET SDK, Streamable HTTP) to the endpoint, call the tool with a timeout, and return the result.

The board's connection list is threaded into the round like `req.BoardGitHub` — added to `RoundRequest` / `AgentRunRequest` and passed through `RunRoundAsync` → `RoundExecutor`. Output flows through the existing **secret-scrub → 48k cap → `function_call_output`** path. The per-call try/catch already in `RoundExecutor` turns MCP failures (auth, server down, timeout) into per-call errors the model recovers from, without aborting the round.

**Run-time context hint:** inject a one-line *"Connected integrations on this board: Slack, Notion"* into the agent's seed/context so the model knows what is actually live (the definition can't vary per board, but the prompt can).

## Component 6 — Web UI

Two touch points, both extending what exists:

- **Board Settings → new "Integrations" tab** ([`BoardSettingsModal.tsx`](../../../src/web/tectika-board/src/components/board/settings/BoardSettingsModal.tsx)): list catalog integrations; *Connect* (paste token + name + validate), status indicator, *Disconnect*. Mirrors the Repository tab. Copy makes the **shared identity explicit** (e.g. *"actions post as the connected Slack bot"*).
- **Agent editor** ([`agents/page.tsx`](../../../src/web/tectika-board/src/app/agents/page.tsx)): the current informational `mcp:` tags become functional per-integration toggles, each with an *"allow write actions"* sub-toggle.

```
Board Settings modal                    Agent editor
┌─ General ─ Repo ─ Workspace ─┐        ┌─ Integrations ──────────────┐
│ ▸ Integrations  ◀ new tab    │        │ ☑ Slack    ☐ allow writes   │
│   Slack    ● Connected  [⋯]  │        │ ☑ Notion   ☑ allow writes   │
│   Notion   ○ Connect         │        │ ☐ Linear                    │
└─ Danger ─────────────────────┘        └─────────────────────────────┘
```

## Error handling & observability

- `McpToolExecutor` traces calls into `RunEvents` exactly like GitHub/workspace.
- No-connection / writes-not-permitted / auth failure / server-down / timeout are all structured per-call errors; the round continues.
- Secret scrubbing already covers tool outputs; the pasted token stays server-side and never enters model context.
- Connection validation failures surface in the UI as `status = Error`.

## Testing

- **Unit:** catalog → Foundry projection (read-only vs write-opted-in); `AgentInstructionsHash` includes `McpServers` + write opt-ins + `McpCatalog.Version`; `McpToolExecutor.CanHandle`; board-connection routing; no-connection error; write-gate (write tool absent from definition unless opted in); name namespacing/collision.
- **Integration:** stand up a **fake in-memory MCP server** (the .NET SDK can host one) to exercise connect → enumerate → validate → call end-to-end with no real SaaS dependency; the connection-validation endpoint is tested against it.
- `MockAgentRuntime` handles MCP tools gracefully (local-dev / mock-DB path).

## Infra & dependencies

- One new NuGet in `agentruntime`: the official **`ModelContextProtocol`** .NET SDK.
- **Key Vault:** reuse the existing secret-read path; *connect* uses `SetSecretAsync`, already used by the GitHub-repo connect flow — same identity/permission. Confirm which service identity (API vs agentruntime) writes secrets today and reuse it.
- Outbound egress to remote MCP endpoints (Container Apps allow it by default).
- **No new Azure resources** for remote-HTTP v1. Per the infra-idempotency rule, keep `infra/` current for any secret-naming convention added (minimal/none expected).

## Rollout (phases — each its own spec → plan → build cycle)

1. **Backend spine (this spec's v1 target)** — `McpCatalog` + one reference integration (a fake MCP server and/or Slack bot-token), connection model + KV storage, `McpToolExecutor`, definition projection, hash changes, runtime threading of board connections, write-gate. Provable end-to-end via tests / CLI before any UI.
2. **Web UI** — Integrations tab + agent enablement toggles.
3. **Curation + per-action write approval** (executor pauses the run for human sign-off before a write executes, then runs it on resume — additive on top of the existing `request_approval` / pending-control / resume machinery) + the run-time "connected integrations" context line.
4. **Out of scope now (later):** OAuth / OBO + per-user identity, Gmail / Canva, managed-gateway catalog source.

## To verify during implementation

- Exact transport API of the `ModelContextProtocol` .NET SDK (Streamable HTTP client) and how it surfaces tool schemas.
- Practical tool-count budget per agent definition (curation keeps it bounded).
- Confirm which service identity writes KV secrets today (API vs agentruntime) and reuse it.
