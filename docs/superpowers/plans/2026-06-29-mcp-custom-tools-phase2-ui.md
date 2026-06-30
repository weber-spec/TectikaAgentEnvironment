# MCP Custom Tools — Phase 2 (Web UI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make MCP integrations usable from the web: a Board Settings **Integrations** tab to connect/disconnect a catalog integration (paste token, validate), and per-integration **enable / allow-writes** toggles in the agent editor.

**Architecture:** Pure frontend over the Phase-1 API (`GET /api/mcp/catalog`, `GET/POST/DELETE /api/boards/{id}/mcp...`). Mirrors the existing GitHub connect pattern: `api.ts` thin fetch wrappers, a `GitHubConnectModal`-style connect modal, a settings tab like the Repository tab, and agent-editor permission toggles like the existing Workspace/GitHub sections.

**Tech Stack:** Next 16 / React 19 client components, TypeScript, Tailwind classes already in the repo. Tests = Node built-in runner (`npm test` → `node --experimental-strip-types --test`). **Caution (`AGENTS.md`):** this is a non-standard Next.js — read `node_modules/next/dist/docs/` before using any Next.js API. Our changes are client components + the api client, so no new Next.js APIs are needed.

**Working dir for all commands:** `src/web/tectika-board`
- Typecheck (fast, per-task): `npx tsc --noEmit`
- Lint: `npm run lint`
- Unit tests: `npm test`
- Full build (final): `npm run build`

**Spec:** [`../specs/2026-06-29-mcp-custom-tools-design.md`](../specs/2026-06-29-mcp-custom-tools-design.md) (Component 6). **Phase-1 API contracts** (already shipped on `main`):
- `GET /api/mcp/catalog` → `[{ id, displayName, description, tokenHint, helpUrl, readToolCount, writeToolCount }]`
- `GET /api/boards/{boardId}/mcp` → `McpConnection[]`
- `POST /api/boards/{boardId}/mcp/connect` body `{ catalogId, displayName?, token }` → `McpConnection` (400 `{error:"ValidationFailed"|"UnknownIntegration"}` on bad token / unknown id)
- `POST /api/boards/{boardId}/mcp/{connectionId}/validate` → `McpConnection`
- `DELETE /api/boards/{boardId}/mcp/{connectionId}` → 204

---

## File Structure

**Modify:**
- `src/lib/types.ts` — add `McpConnection`, `McpConnectionStatus`, `McpCatalogEntry`; extend `Board` (`mcpConnections`) and `AgentRole` (`mcpWriteEnabled`).
- `src/lib/api.ts` — add the `api.mcp` section.
- `src/components/board/settings/BoardSettingsModal.tsx` — add the `integrations` tab.
- `src/app/agents/page.tsx` — MCP enablement in `RoleEditor`; tags in `RoleCard`; `mcpWriteEnabled` in `newAgent()`.

**Create:**
- `src/components/board/settings/McpIntegrationsTab.tsx` — the tab body (catalog list + per-integration connect/disconnect).
- `src/components/board/settings/McpConnectModal.tsx` — connect form for one catalog integration (mirrors `GitHubConnectModal`).
- `src/lib/__tests__/mcp-api.test.ts` — api-client route/method/body test.

---

## Task 1: Types

**File:** Modify `src/lib/types.ts`

- [ ] **Step 1: Add the MCP types**

After the `GitHubPermissions` interface (around line 61), add:

```ts
export type McpConnectionStatus = 'Connected' | 'Error' | 'Disconnected';

/** A per-board MCP integration connection (mirrors C# McpConnection). */
export interface McpConnection {
  connectionId: string;
  catalogId: string;
  displayName: string;
  secretName: string;
  status: McpConnectionStatus;
  lastValidatedAt?: string | null;
  createdBy?: string | null;
  createdAt: string;
}

/** GET /api/mcp/catalog item — UI projection (no endpoint/auth internals). */
export interface McpCatalogEntry {
  id: string;
  displayName: string;
  description: string;
  tokenHint: string;
  helpUrl?: string | null;
  readToolCount: number;
  writeToolCount: number;
}
```

- [ ] **Step 2: Extend `Board`**

In the `Board` interface, add after the `github?: GitHubRepoConnection | null;` field (READ the file to find it — it's near the workspace fields):

```ts
  mcpConnections: McpConnection[];
```

- [ ] **Step 3: Extend `AgentRole`**

In the `AgentRole` interface, add right after `mcpServers: string[];` (line ~190):

```ts
  mcpWriteEnabled: string[];
```

- [ ] **Step 4: Typecheck**

Run (from `src/web/tectika-board`): `npx tsc --noEmit`
Expected: it will report errors in `agents/page.tsx` (the `newAgent()` literal is now missing `mcpWriteEnabled`) — that's expected and fixed in Task 5. **No errors in `types.ts` itself.** If `tsc` reports unrelated pre-existing errors, note them; only `types.ts` must be clean.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts
git commit -m "feat(web): MCP types — McpConnection, McpCatalogEntry, Board.mcpConnections, AgentRole.mcpWriteEnabled"
```

---

## Task 2: API client + test (TDD)

**Files:** Modify `src/lib/api.ts`; Create `src/lib/__tests__/mcp-api.test.ts`

- [ ] **Step 1: Write the failing test**

Create `src/lib/__tests__/mcp-api.test.ts` (mirrors `board-settings-api.test.ts` exactly — same resolve-hook + fetch-stub harness):

```ts
// Verifies the MCP client methods build the right routes/methods/bodies.
// Run: node --experimental-strip-types --test src/lib/__tests__/mcp-api.test.ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as nodeModule from 'node:module';

type ResolveCtx = Record<string, unknown>;
type ResolveResult = { url: string; format?: string | null };
type NextResolve = (specifier: string, context: ResolveCtx) => ResolveResult;
const registerHooks = (nodeModule as unknown as {
  registerHooks: (hooks: { resolve: (s: string, c: ResolveCtx, n: NextResolve) => ResolveResult }) => void;
}).registerHooks;
registerHooks({
  resolve(specifier: string, context: ResolveCtx, nextResolve: NextResolve): ResolveResult {
    if (/^\.\.?\//.test(specifier) && !/\.[a-z]+$/i.test(specifier)) {
      try { return nextResolve(specifier + '.ts', context); } catch { return nextResolve(specifier, context); }
    }
    return nextResolve(specifier, context);
  },
});

const { api } = await import('../api.ts');

interface Call { url: string; method: string; body?: string }
function stubFetch(): { calls: Call[]; restore: () => void } {
  const calls: Call[] = [];
  const orig = globalThis.fetch;
  globalThis.fetch = (async (url: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string | undefined });
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('mcp client builds correct routes/methods/bodies', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.mcp.catalog();
    await api.mcp.connections('b1');
    await api.mcp.connect('b1', { catalogId: 'slack', displayName: 'My Slack', token: 'xoxb-abc' });
    await api.mcp.validate('b1', 'c1');
    await api.mcp.disconnect('b1', 'c1');

    assert.ok(calls[0].url.endsWith('/api/mcp/catalog')); assert.equal(calls[0].method, 'GET');
    assert.ok(calls[1].url.endsWith('/api/boards/b1/mcp')); assert.equal(calls[1].method, 'GET');
    assert.ok(calls[2].url.endsWith('/api/boards/b1/mcp/connect')); assert.equal(calls[2].method, 'POST');
    assert.match(calls[2].body!, /"catalogId":"slack"/);
    assert.match(calls[2].body!, /"token":"xoxb-abc"/);
    assert.ok(calls[3].url.endsWith('/api/boards/b1/mcp/c1/validate')); assert.equal(calls[3].method, 'POST');
    assert.ok(calls[4].url.endsWith('/api/boards/b1/mcp/c1')); assert.equal(calls[4].method, 'DELETE');
  } finally { restore(); }
});
```

- [ ] **Step 2: Run to verify it fails**

Run (from `src/web/tectika-board`): `node --experimental-strip-types --test src/lib/__tests__/mcp-api.test.ts`
Expected: FAIL — `api.mcp` is undefined.

- [ ] **Step 3: Add the `api.mcp` section**

In `src/lib/api.ts`:

(a) Extend the type import (the big `import type { ... } from './types';` block) to include `McpConnection, McpCatalogEntry`.

(b) Add this section inside the `export const api = { ... }` object (e.g. right after the `agentRoles: { ... },` block):

```ts
  mcp: {
    catalog: () => fetchApi<McpCatalogEntry[]>('/api/mcp/catalog'),
    connections: (boardId: string) => fetchApi<McpConnection[]>(`/api/boards/${boardId}/mcp`),
    connect: (boardId: string, input: { catalogId: string; displayName?: string; token: string }) =>
      fetchApi<McpConnection>(`/api/boards/${boardId}/mcp/connect`, { method: 'POST', body: JSON.stringify(input) }),
    validate: (boardId: string, connectionId: string) =>
      fetchApi<McpConnection>(`/api/boards/${boardId}/mcp/${connectionId}/validate`, { method: 'POST' }),
    disconnect: (boardId: string, connectionId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/mcp/${connectionId}`, { method: 'DELETE' }),
  },
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `node --experimental-strip-types --test src/lib/__tests__/mcp-api.test.ts`
Expected: PASS (1 test). Also `npm test` to confirm the whole suite is green.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/api.ts src/web/tectika-board/src/lib/__tests__/mcp-api.test.ts
git commit -m "feat(web): api.mcp client (catalog, connections, connect, validate, disconnect)"
```

---

## Task 3: Connect modal

**File:** Create `src/components/board/settings/McpConnectModal.tsx`

This mirrors `GitHubConnectModal` (token field, inline error on validation failure, Key Vault note). It connects ONE catalog integration.

- [ ] **Step 1: Create the component**

```tsx
'use client';

import { useState } from 'react';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api';
import type { McpCatalogEntry } from '@/lib/types';
import { toast } from '@/lib/toast';

/** Pull a readable message out of an ApiError whose message is "API 400: {json}". */
function extractError(err: ApiError): string {
  const brace = err.message.indexOf('{');
  if (brace >= 0) {
    try {
      const body = JSON.parse(err.message.slice(brace));
      if (body?.detail) return String(body.detail);
      if (body?.error === 'ValidationFailed') return 'That credential was rejected by the service. Check the token and try again.';
      if (body?.error) return String(body.error);
    } catch { /* not JSON */ }
  }
  return 'Could not connect. Check the token and try again.';
}

export function McpConnectModal({
  boardId, entry, onClose, onConnected,
}: {
  boardId: string;
  entry: McpCatalogEntry;
  onClose: () => void;
  onConnected: () => void;
}) {
  const [displayName, setDisplayName] = useState(entry.displayName);
  const [token, setToken] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleConnect = async () => {
    if (!token.trim()) return;
    setSaving(true);
    setError(null);
    try {
      await api.mcp.connect(boardId, { catalogId: entry.id, displayName: displayName.trim() || undefined, token: token.trim() });
      toast(`${entry.displayName} connected`, 'success');
      onConnected();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) setError(extractError(err));
      else { toast('Could not connect', 'error'); }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      width={480}
      z={1300}
      title={<span className="text-[var(--foreground)]">Connect {entry.displayName}</span>}
      footer={
        <div className="flex justify-end gap-2 w-full">
          <Button onClick={onClose} disabled={saving}>Cancel</Button>
          <Button variant="primary" onClick={handleConnect} disabled={saving || !token.trim()}>
            {saving ? 'Connecting…' : 'Connect'}
          </Button>
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div className="text-sm text-[#e2445c] bg-[#e2445c11] px-3 py-2 rounded-lg">{error}</div>
        )}
        <p className="text-[13px] text-[var(--muted)]">{entry.description}</p>
        <p className="text-[12px] text-[var(--muted)] bg-[var(--surface)] px-3 py-2 rounded-lg">
          Actions run as this shared connection for the whole board (e.g. messages post as the connected bot).
        </p>

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Connection name</span>
          <input className="inp mt-1 w-full" value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder={entry.displayName} />
        </label>

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">{entry.tokenHint}</span>
          <input
            className="inp mt-1 w-full"
            type="password"
            placeholder="Paste token"
            value={token}
            onChange={e => { setToken(e.target.value); setError(null); }}
          />
          <p className="text-[11px] text-[var(--muted)] mt-1">
            Validated on connect and stored securely in Azure Key Vault.
            {entry.helpUrl && <> <a href={entry.helpUrl} target="_blank" rel="noreferrer" className="underline">Where do I get this?</a></>}
          </p>
        </label>
      </div>
    </Modal>
  );
}
```

- [ ] **Step 2: Typecheck**

Run: `npx tsc --noEmit`
Expected: no NEW errors in `McpConnectModal.tsx` (the `Modal` `z` prop is already used elsewhere with `z={1300}`; confirm `Modal` accepts `z` — it does in `BoardSettingsModal`). The pre-existing `agents/page.tsx` error from Task 1 may still show until Task 5.

- [ ] **Step 3: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/McpConnectModal.tsx
git commit -m "feat(web): McpConnectModal — connect a catalog integration with token validation"
```

---

## Task 4: Integrations tab + wire into Board Settings

**Files:** Create `src/components/board/settings/McpIntegrationsTab.tsx`; Modify `src/components/board/settings/BoardSettingsModal.tsx`

- [ ] **Step 1: Create the tab component**

```tsx
'use client';

import { useEffect, useState, useCallback } from 'react';
import { Button } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { McpCatalogEntry, McpConnection } from '@/lib/types';
import { McpConnectModal } from './McpConnectModal';

export function McpIntegrationsTab({ boardId }: { boardId: string }) {
  const [catalog, setCatalog] = useState<McpCatalogEntry[] | null>(null);
  const [connections, setConnections] = useState<McpConnection[]>([]);
  const [connecting, setConnecting] = useState<McpCatalogEntry | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const refreshConnections = useCallback(async () => {
    try { setConnections(await api.mcp.connections(boardId)); } catch { /* shown via toast on actions */ }
  }, [boardId]);

  useEffect(() => {
    api.mcp.catalog().then(setCatalog).catch(() => setCatalog([]));
    refreshConnections();
  }, [refreshConnections]);

  const disconnect = async (conn: McpConnection) => {
    setBusyId(conn.connectionId);
    try {
      await api.mcp.disconnect(boardId, conn.connectionId);
      toast('Disconnected', 'success');
      await refreshConnections();
    } catch { toast('Could not disconnect', 'error'); }
    finally { setBusyId(null); }
  };

  if (catalog === null) return <p className="text-sm text-[var(--muted)]">Loading integrations…</p>;

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-[var(--muted)]">
        Connect external tools so agents on this board can use them. Connections are shared by the whole board;
        actions run as the connected account. Enable an integration per-agent in the Agents editor.
      </p>

      <div className="flex flex-col gap-2">
        {catalog.map(entry => {
          const conn = connections.find(c => c.catalogId === entry.id);
          return (
            <div key={entry.id} className="flex items-start justify-between gap-3 border border-[var(--border)] rounded-lg p-3">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-[var(--foreground)]">{entry.displayName}</span>
                  {conn && (
                    conn.status === 'Connected'
                      ? <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400">connected</span>
                      : <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-red-500/15 text-red-600 dark:text-red-400">error</span>
                  )}
                </div>
                <div className="text-xs text-[var(--muted)] mt-0.5">{entry.description}</div>
                <div className="text-[11px] text-[var(--muted)] mt-0.5">
                  {entry.readToolCount} read · {entry.writeToolCount} write {conn ? `· “${conn.displayName}”` : ''}
                </div>
              </div>
              <div className="shrink-0">
                {conn ? (
                  <Button variant="danger" onClick={() => disconnect(conn)} disabled={busyId === conn.connectionId}>
                    {busyId === conn.connectionId ? 'Disconnecting…' : 'Disconnect'}
                  </Button>
                ) : (
                  <Button variant="primary" onClick={() => setConnecting(entry)}>Connect</Button>
                )}
              </div>
            </div>
          );
        })}
        {catalog.length === 0 && <p className="text-sm text-[var(--muted)]">No integrations available.</p>}
      </div>

      {connecting && (
        <McpConnectModal
          boardId={boardId}
          entry={connecting}
          onClose={() => setConnecting(null)}
          onConnected={refreshConnections}
        />
      )}
    </div>
  );
}
```

- [ ] **Step 2: Wire the tab into `BoardSettingsModal`**

In `src/components/board/settings/BoardSettingsModal.tsx`:

(a) Add the import near the other settings imports:
```tsx
import { McpIntegrationsTab } from './McpIntegrationsTab';
```

(b) Extend the `TabId` type:
```tsx
type TabId = 'general' | 'repository' | 'workspace' | 'integrations' | 'danger';
```

(c) Add the tab to the `tabs` array, after the `workspace` entry (use an existing icon — `Icon.bolt` or `Icon.flow`; READ `@/components/ui/icons` for an available one, e.g. `Icon.bolt`):
```tsx
    { id: 'integrations' as const, label: 'Integrations', icon: <Icon.bolt size={15} />, show: true },
```

(d) Render the tab body, after the `{tab === 'workspace' && ...}` block:
```tsx
            {tab === 'integrations' && <McpIntegrationsTab boardId={board.id} />}
```

- [ ] **Step 3: Typecheck + lint**

Run: `npx tsc --noEmit` then `npm run lint`
Expected: no NEW errors/warnings in the two files. (`agents/page.tsx` may still error until Task 5 — that's expected.) If `Icon.bolt` doesn't exist, pick a real one from `@/components/ui/icons`.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/McpIntegrationsTab.tsx src/web/tectika-board/src/components/board/settings/BoardSettingsModal.tsx
git commit -m "feat(web): Board Settings → Integrations tab (connect/disconnect MCP integrations)"
```

---

## Task 5: Agent editor — enable + allow-writes toggles

**File:** Modify `src/app/agents/page.tsx`

- [ ] **Step 1: Fix `newAgent()` and add catalog fetch**

(a) In `newAgent()`, add `mcpWriteEnabled: []` next to `mcpServers: []`:
```tsx
    systemPrompt: 'You are a helpful agent.', tools: [], mcpServers: [], mcpWriteEnabled: [],
```

(b) Add the import for the catalog type/api (api is already imported):
```tsx
import type { AgentRole, McpCatalogEntry } from '@/lib/types';
```
(replace the existing `import type { AgentRole } from '@/lib/types';`)

- [ ] **Step 2: Add the MCP section to `RoleEditor`**

Inside `RoleEditor`, after the existing state/handlers, add catalog state + toggle helpers:

```tsx
  const [catalog, setCatalog] = useState<McpCatalogEntry[]>([]);
  useEffect(() => { api.mcp.catalog().then(setCatalog).catch(() => setCatalog([])); }, []);

  const mcpEnabled = (id: string) => r.mcpServers.includes(id);
  const toggleMcp = (id: string, on: boolean) =>
    setR(prev => ({
      ...prev,
      mcpServers: on ? [...new Set([...prev.mcpServers, id])] : prev.mcpServers.filter(x => x !== id),
      // Disabling an integration also revokes its write opt-in.
      mcpWriteEnabled: on ? prev.mcpWriteEnabled : prev.mcpWriteEnabled.filter(x => x !== id),
    }));
  const toggleMcpWrite = (id: string, on: boolean) =>
    setR(prev => ({
      ...prev,
      mcpWriteEnabled: on ? [...new Set([...prev.mcpWriteEnabled, id])] : prev.mcpWriteEnabled.filter(x => x !== id),
    }));
```

(`useState` and `useEffect` are already imported at the top of the file — confirm; if not, add them to the `react` import.)

Then add this block in the editor JSX, after the GitHub permissions section (before the `Can push code / Can deploy` row):

```tsx
        {/* Layer 3: MCP integrations */}
        {catalog.length > 0 && (
          <div className="rounded-lg border border-[var(--border)] p-3">
            <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold flex items-center gap-1.5 mb-2">
              <Icon.bolt size={13} /> Integrations
            </span>
            <div className="flex flex-col gap-2">
              {catalog.map(entry => {
                const on = mcpEnabled(entry.id);
                return (
                  <div key={entry.id} className="flex flex-col gap-1">
                    <label className="flex items-center gap-2 text-sm text-[var(--foreground)]">
                      <input type="checkbox" checked={on}
                        onChange={e => toggleMcp(entry.id, e.target.checked)}
                        className="accent-[var(--primary)]" />
                      {entry.displayName}
                      <span className="text-[11px] text-[var(--muted)]">({entry.readToolCount} read · {entry.writeToolCount} write)</span>
                    </label>
                    {on && entry.writeToolCount > 0 && (
                      <label className="flex items-center gap-2 text-[13px] text-[var(--foreground)] ml-6">
                        <input type="checkbox" checked={r.mcpWriteEnabled.includes(entry.id)}
                          onChange={e => toggleMcpWrite(entry.id, e.target.checked)}
                          className="accent-[var(--primary)]" />
                        Allow write actions
                        <span className="text-[11px] text-[var(--muted)]">(send/post/create)</span>
                      </label>
                    )}
                  </div>
                );
              })}
            </div>
            <p className="text-[11px] text-[var(--muted)] mt-2">Connect integrations per board in Board Settings → Integrations.</p>
          </div>
        )}
```

- [ ] **Step 3: Update `RoleCard` tags**

In `RoleCard`, replace the existing mcp tag line:
```tsx
        {role.mcpServers.map(m => <Tag key={m} label={`mcp:${m}`} hex="#a25ddc" />)}
```
with (show a write marker when the integration is write-enabled):
```tsx
        {role.mcpServers.map(m => <Tag key={m} label={role.mcpWriteEnabled?.includes(m) ? `mcp:${m} ✎` : `mcp:${m}`} hex="#a25ddc" />)}
```

- [ ] **Step 4: Typecheck + lint**

Run: `npx tsc --noEmit` then `npm run lint`
Expected: clean (Task 1's expected `agents/page.tsx` error is now resolved). No new lint warnings.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/app/agents/page.tsx
git commit -m "feat(web): agent editor — per-integration enable + allow-writes toggles"
```

---

## Task 6: Final sweep

- [ ] **Step 1: Full typecheck, lint, unit tests**

From `src/web/tectika-board`:
- `npx tsc --noEmit` → no errors
- `npm run lint` → no new errors
- `npm test` → all green (includes the new `mcp-api.test.ts`)

- [ ] **Step 2: Production build**

Run: `npm run build`
Expected: build succeeds (this is the authoritative typecheck for the App Router). Investigate any error before finishing.

- [ ] **Step 3: Commit (only if a fix was needed)**

```bash
git add -A && git commit -m "chore(web): Phase 2 MCP UI — build/lint/test green"
```

---

## Done-when (Phase 2 acceptance)

- Board Settings has an **Integrations** tab listing catalog integrations; Connect opens a modal that validates the token and stores it; a connected integration shows status + Disconnect.
- The agent editor shows each catalog integration with an enable checkbox and, when enabled and the integration has write tools, an **Allow write actions** sub-toggle; both persist via `mcpServers` / `mcpWriteEnabled`.
- `RoleCard` reflects enabled integrations (with a write marker).
- `npm run build`, `npm run lint`, `npm test` all green.

## Manual visual QA (after the build is green)

Optional but recommended (see the "running AgentBoard for visual QA" runbook): launch the app against the mock DB and screenshot (a) Board Settings → Integrations and (b) the agent editor's Integrations section, to confirm layout/states. Not a blocking step for branch completion.

## Notes carried forward

- Connect/disconnect manage state locally in the tab via `api.mcp.connections` (re-fetched after each change); the parent `Board.mcpConnections` isn't relied on for display, so no `onBoardUpdated` plumbing is needed.
- The UI presents **one connection per catalog integration** per board (Connect → Disconnect → reconnect), even though the backend allows multiple — keeps the model aligned with the one-repo-per-board GitHub mental model.
- Phase 3 (per-action write approval + run-time "connected integrations" context line) and OAuth/Gmail/Canva remain out of scope.
