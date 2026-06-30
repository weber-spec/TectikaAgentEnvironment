# Email Sending-Domain Verification — Design

- **Date:** 2026-06-30
- **Status:** Approved (design)
- **Depends on:** the merged Email (Resend) first-party integration (`McpBackend.FirstParty`, `ResendEmailConnector`, per-board `McpConnection`).

## Problem

Agents can send email through a board's connected Resend account, but only in **test mode**: the `from` is forced to `onboarding@resend.dev`, which can only reach the account owner / Resend test sinks. To let agents send to **anyone**, the board's Resend account needs a **verified sending domain**, and agents must send `from` an address at that domain.

Resend verifies a **domain** (via DNS: SPF/DKIM/DMARC), not a single address. Once a domain is `verified`, you can send from any local-part `@that-domain`. You can only verify a domain you control its DNS for — so "send from gmail.com" is not possible; sending *as* a real mailbox provider account is a separate (deferred) OAuth integration.

## Goal

On a board's connected **Email** integration, an admin can, entirely in Tectika:
1. **Add** a sending domain → see the exact DNS records Resend requires.
2. **Verify** it → watch status move to `verified`.
3. Set a **default `from`** address on the connection.

Then agents' `send_email` uses that default sender (or any address on a verified domain) and can reach any recipient.

## Non-goals (YAGNI)

- No mirroring/persisting of Resend domain state in Cosmos (Resend is the source of truth).
- No `domain.updated` webhooks — we poll `GET /domains/{id}` on demand.
- No multi-connection-per-catalog UX — assume one Email connection per board (the executor already uses the first `Connected` one).
- No Gmail/OAuth "send as a user" path (separate, deferred).
- No DNS automation — the admin adds records at their DNS provider.

## Architecture — live proxy to Resend

All domain operations proxy straight to the Resend Domains API using the connection's API key (resolved server-side from Key Vault; never sent to the browser). We store exactly **one** new piece of state: the connection's default `from`.

```
Browser (Board Settings → Integrations → Email)
  → API: BoardEmailController  (resolves board + email connection + KV key)
      → ResendDomainsClient (HTTP) → https://api.resend.com/domains…
  ← domain list / records / status   (no API key, no secrets in payload)
```

### Resend Domains API used
- `POST /domains {name}` → `{id, name, status, records[]}` (records = the DNS to add).
- `GET /domains` → list `{id, name, status}`.
- `GET /domains/{id}` → `{id, name, status, records[]}` (for polling).
- `POST /domains/{id}/verify` → triggers **async** verification (status → `pending` → `verified`/`failed`). Caller polls `GET`.
- `DELETE /domains/{id}`.

Status values surfaced as-is: `not_started | pending | verified | partially_verified | partially_failed | failed | temporary_failure`.

## Backend components

1. **`ResendDomainsClient`** (`src/agentruntime/Mcp/`, alongside `ResendEmailConnector`) — thin `IHttpClientFactory`-based client: `ListDomainsAsync`, `CreateDomainAsync(name)`, `GetDomainAsync(id)`, `VerifyDomainAsync(id)`, `DeleteDomainAsync(id)`, each taking the resolved API key as a bearer token. Returns typed DTOs (`ResendDomain { Id, Name, Status, Records[] }`, `ResendDnsRecord { Type, Name, Value, Ttl, Priority? }`). Never logs the key. 30s timeout.
2. **`McpConnection.DefaultFrom` (string?)** — new optional field; the only persisted addition.
3. **`BoardEmailController`** — `[Authorize]`, `[Route("api/boards/{boardId}/email")]`, scoped to the board's `email` connection (404 if no `Connected` email connection). Endpoints:
   - `GET    /domains` — list.
   - `POST   /domains {name}` — create → returns DNS records.
   - `GET    /domains/{id}` — get/poll status + records.
   - `POST   /domains/{id}/verify` — trigger verify.
   - `DELETE /domains/{id}` — remove.
   - `PUT    /from {from}` — set `DefaultFrom` on the connection (basic email-shape validation; persisted via `UpdateBoardAsync`).
   Each resolves the connection's KV key, calls the client, and maps Resend failures to clean errors (mirrors the connect handler: clear message, no 500, no secret leakage).

## `send_email` change

- The catalog `send_email` tool's `from` becomes **optional** (description updated).
- The connector seam `IFirstPartyConnector.CallAsync` gains the resolved `McpConnection` so the connector can read `DefaultFrom`: `CallAsync(string toolName, JsonElement args, string token, McpConnection connection, CancellationToken ct)`. `McpToolExecutor` already has the resolved `conn`; it passes it through.
- `ResendEmailConnector.send_email`: `from = args.from ?? connection.DefaultFrom`; if both are empty → clean error ("No sender address is configured — set a default From in Board Settings → Integrations → Email."). If `from` is set but not on a verified domain, Resend rejects it and we surface that error verbatim-ish.
- `McpCatalog.Version` bumps (tool description/required change) so affected agents republish.

## UI

Board Settings → Integrations → **Email** connection card expands to a **Sending domains** panel:
- **Add domain** input + button → on success, the domain appears with status `not_started/pending` and its **DNS records** in a copyable table (`Type / Name / Value`).
- Per-domain **status badge** and a **Verify** button → calls verify, then polls `GET /domains/{id}` a few times (with a manual "Refresh" too) until `verified`/`failed`.
- **Default From** field (e.g. `agents@tectika.com`) with Save → `PUT /from`.
- Remove (DELETE) per domain.
- The page is already catalog-driven; this is a new panel keyed off the email connection. New client methods under `api.email.*` mirror the routes.

## Security

- The Resend API key never leaves the server; domain endpoints resolve it from Key Vault per request.
- Responses contain only public DNS values + domain metadata — no secrets. Nothing logs the key.
- All endpoints are `[Authorize]` and tenant/board-scoped (same pattern as `BoardMcpConnectionsController`).

## Error handling

- Resend non-2xx → clean `{ error, detail }` (e.g. invalid domain name, plan limits, not found), never a raw 500.
- `from` set to an unverified-domain address → the send fails with Resend's error surfaced to the agent (already wrapped by the connector).

## Testing

- `ResendDomainsClient`: request shape (method/path/bearer) + DTO parsing + error mapping (stubbed `HttpMessageHandler`).
- `BoardEmailController`: each endpoint with a fake client + fake Cosmos/secrets (success + no-connection 404 + Resend-failure mapping); `PUT /from` persists `DefaultFrom`.
- `ResendEmailConnector.send_email`: uses `DefaultFrom` when `from` omitted; clean error when neither set.
- Web: `api.email.*` route/method/body test.

## Rollout / infra

- **No new infra**: reuses the existing per-board Key Vault secret (the connection's API key) and `AddHttpClient()`. The KV write/read permission fix (Secrets Officer) is handled separately.
- No new Cosmos container; `DefaultFrom` is a field on the existing board document.

## Open questions

None blocking. (Future: per-agent sender allow-list; multiple email connections per board; webhook-driven status instead of polling.)
