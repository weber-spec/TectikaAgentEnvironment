# Code Viewer — Plan 2B (Frontend: RepoView) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a board-level **Repo** surface to the web app — a pinned tab that opens a `RepoView` with Code (file tree + syntax-highlighted viewer), History (commits), and Pull Requests sub-tabs and a branch switcher — backed by the Plan 2A read endpoints.

**Architecture:** `BoardView` gains a `showRepo` mode toggled by a pinned Repo tab in the view-tab row; when on, it renders `<RepoView>` instead of the task views. `RepoView` owns branch + sub-tab state and calls a new `api.repo` client. A 409 from the API renders a "connect GitHub" prompt. Code highlighting uses Shiki via a small async helper with a `<pre>` fallback.

**Tech Stack:** Next.js 16 / React 19 / TypeScript / Tailwind v4, Shiki (new dep), `node --test` for pure helpers.

**Testing reality:** the web app has no component-test runner (jest/vitest/RTL are not installed; `package.json` `test` = `node --test`). So this plan unit-tests **pure helpers** with `node --test`, and verifies components with `npx tsc --noEmit` + `npm run lint` (no new errors vs the 7e/6w baseline) + `npm run build` + a manual render check. Strict red-green TDD applies to the helpers; components are build-and-verify.

**Builds on:** Plan 2A (merged to main) — endpoints `GET /api/boards/{boardId}/repo/{meta|branches|tree|file|commits|pulls|pulls/{n}}`, the `409 { error: "GitHubNotConnected" }` body, and `FileContent.isBinary`/`text=null`. Completes Spec 2 (`docs/superpowers/specs/2026-06-17-code-viewer-design.md`).

---

## File Structure

**Create:**
- `src/web/tectika-board/src/lib/highlight.ts` — `languageForPath(path)` (pure) + `highlightToHtml(code, lang)` (Shiki).
- `src/web/tectika-board/src/lib/highlight.test.ts` — `node --test` for `languageForPath`.
- `src/web/tectika-board/src/components/board/repo/RepoView.tsx` — shell: sub-tabs, branch switcher, no-GitHub prompt.
- `src/web/tectika-board/src/components/board/repo/CodeTab.tsx` — file tree + file viewer.
- `src/web/tectika-board/src/components/board/repo/HistoryTab.tsx` — commit list.
- `src/web/tectika-board/src/components/board/repo/PullsTab.tsx` — PR list.

**Modify:**
- `src/web/tectika-board/src/lib/types.ts` — repo DTO types.
- `src/web/tectika-board/src/lib/api.ts` — `api.repo` group.
- `src/web/tectika-board/src/components/board/ViewTabs.tsx` — optional pinned Repo tab + view-select callback.
- `src/web/tectika-board/src/components/board/BoardView.tsx` — `showRepo` state + conditional render.
- `src/web/tectika-board/package.json` — add `shiki`.

All work is in `src/web/tectika-board`; run npm/tsc/lint/build from that directory.

---

## Task 1: Repo types + `api.repo` client

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`, `src/web/tectika-board/src/lib/api.ts`

No unit test (type + fetch-wrapper additions; verified by tsc).

- [ ] **Step 1: Add the DTO types.** In `src/web/tectika-board/src/lib/types.ts`, at the end of the file, add (these mirror the C# records from Plan 2A — camelCase JSON):

```typescript
// ── Code viewer (Spec 2) ───────────────────────────────────────────────
export interface RepoMeta { defaultBranch: string; description?: string | null; private: boolean; }
export interface BranchInfo { name: string; commitSha: string; }
export interface TreeEntry { name: string; path: string; type: 'file' | 'dir'; size: number; }
export interface FileContent { path: string; sha: string; size: number; isBinary: boolean; text: string | null; }
export interface CommitInfo { sha: string; message: string; author: string; date: string; url: string; }
export interface PullRequestInfo { number: number; title: string; state: string; author: string; head: string; base: string; url: string; createdAt: string; }
```

- [ ] **Step 2: Add the `api.repo` client.** In `src/web/tectika-board/src/lib/api.ts`:

(a) Extend the type import to include the new types — add to the existing `import type { ... } from './types';`:
```typescript
  RepoMeta, BranchInfo, TreeEntry, FileContent, CommitInfo, PullRequestInfo,
```

(b) Inside the `export const api = { ... }` object, add a `repo` group (place it after the `boards` group; mind the trailing comma):
```typescript
  repo: {
    meta: (boardId: string) => fetchApi<RepoMeta>(`/api/boards/${boardId}/repo/meta`),
    branches: (boardId: string) => fetchApi<BranchInfo[]>(`/api/boards/${boardId}/repo/branches`),
    tree: (boardId: string, ref?: string, path?: string) =>
      fetchApi<TreeEntry[]>(`/api/boards/${boardId}/repo/tree?ref=${encodeURIComponent(ref ?? '')}&path=${encodeURIComponent(path ?? '')}`),
    file: (boardId: string, path: string, ref?: string) =>
      fetchApi<FileContent>(`/api/boards/${boardId}/repo/file?ref=${encodeURIComponent(ref ?? '')}&path=${encodeURIComponent(path)}`),
    commits: (boardId: string, ref?: string, path?: string, page = 1) =>
      fetchApi<CommitInfo[]>(`/api/boards/${boardId}/repo/commits?ref=${encodeURIComponent(ref ?? '')}&path=${encodeURIComponent(path ?? '')}&page=${page}`),
    pulls: (boardId: string, state = 'open') =>
      fetchApi<PullRequestInfo[]>(`/api/boards/${boardId}/repo/pulls?state=${encodeURIComponent(state)}`),
  },
```

- [ ] **Step 3: Type-check.** Run: `cd src/web/tectika-board && npx tsc --noEmit` → no errors.

- [ ] **Step 4: Commit:**
```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts
git commit -m "feat(web): repo DTO types + api.repo client for code viewer"
```

---

## Task 2: Shiki highlight helper + language map

**Files:**
- Modify: `src/web/tectika-board/package.json` (add `shiki`)
- Create: `src/web/tectika-board/src/lib/highlight.ts`, `src/web/tectika-board/src/lib/highlight.test.ts`

- [ ] **Step 1: Add the dependency.** Run: `cd src/web/tectika-board && npm install shiki` (this updates package.json + package-lock.json).

- [ ] **Step 2: Write the failing test.** Create `src/web/tectika-board/src/lib/highlight.test.ts`:

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { languageForPath } from './highlight.ts';

test('maps known extensions to shiki languages', () => {
  assert.equal(languageForPath('src/Cart.tsx'), 'tsx');
  assert.equal(languageForPath('a/b/main.ts'), 'typescript');
  assert.equal(languageForPath('x.py'), 'python');
  assert.equal(languageForPath('Program.cs'), 'csharp');
  assert.equal(languageForPath('data.json'), 'json');
  assert.equal(languageForPath('README.md'), 'markdown');
});

test('unknown or missing extension falls back to text', () => {
  assert.equal(languageForPath('LICENSE'), 'text');
  assert.equal(languageForPath('weird.xyz'), 'text');
  assert.equal(languageForPath(''), 'text');
});
```

- [ ] **Step 3: Run, verify FAIL.** Run: `cd src/web/tectika-board && node --test src/lib/highlight.test.ts` → fails (module/export missing). (Node 22+ runs `.ts` via type-stripping; if this Node cannot import `.ts`, the implementer should confirm the repo's existing `node --test` setup handles TS — match whatever the existing `*.test.ts` files in the repo do; if none exist, write the test as `.mjs` importing the compiled helper, or keep `languageForPath` logic in a plain `.ts` and test via `tsx`. Use the same mechanism the repo's `npm test` script already supports.)

- [ ] **Step 4: Implement.** Create `src/web/tectika-board/src/lib/highlight.ts`:

```typescript
// Syntax highlighting for the code viewer. languageForPath is pure (unit-tested);
// highlightToHtml lazy-loads Shiki on the client.

const EXT_TO_LANG: Record<string, string> = {
  ts: 'typescript', tsx: 'tsx', js: 'javascript', jsx: 'jsx', mjs: 'javascript', cjs: 'javascript',
  json: 'json', css: 'css', scss: 'scss', html: 'html', md: 'markdown', mdx: 'markdown',
  py: 'python', cs: 'csharp', go: 'go', rs: 'rust', java: 'java', rb: 'ruby', php: 'php',
  c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', sh: 'bash', bash: 'bash', yml: 'yaml', yaml: 'yaml',
  toml: 'toml', xml: 'xml', sql: 'sql', kt: 'kotlin', swift: 'swift', dockerfile: 'docker',
};

export function languageForPath(path: string): string {
  const name = path.split('/').pop() ?? '';
  if (name.toLowerCase() === 'dockerfile') return 'docker';
  const dot = name.lastIndexOf('.');
  if (dot <= 0) return 'text';
  const ext = name.slice(dot + 1).toLowerCase();
  return EXT_TO_LANG[ext] ?? 'text';
}

/** Highlight code to HTML using Shiki. Returns null on failure (caller shows a plain <pre>). */
export async function highlightToHtml(code: string, lang: string): Promise<string | null> {
  try {
    const { codeToHtml } = await import('shiki');
    return await codeToHtml(code, { lang: lang === 'text' ? 'text' : lang, theme: 'github-dark' });
  } catch {
    return null;
  }
}
```

- [ ] **Step 5: Run, verify PASS.** Run: `cd src/web/tectika-board && node --test src/lib/highlight.test.ts` → both tests pass.

- [ ] **Step 6: Commit:**
```bash
git add src/web/tectika-board/package.json src/web/tectika-board/package-lock.json src/web/tectika-board/src/lib/highlight.ts src/web/tectika-board/src/lib/highlight.test.ts
git commit -m "feat(web): add Shiki highlight helper + languageForPath (tested)"
```

---

## Task 3: `RepoView` shell + board wiring

**Files:**
- Create: `src/web/tectika-board/src/components/board/repo/RepoView.tsx`
- Modify: `src/web/tectika-board/src/components/board/ViewTabs.tsx`, `src/web/tectika-board/src/components/board/BoardView.tsx`

- [ ] **Step 1: Create the `RepoView` shell.** Create `src/web/tectika-board/src/components/board/repo/RepoView.tsx`:

```tsx
'use client';

import React, { useEffect, useState } from 'react';
import { api, ApiError } from '@/lib/api';
import type { RepoMeta } from '@/lib/types';
import { Icon } from '@/components/ui/icons';
import { CodeTab } from './CodeTab';
import { HistoryTab } from './HistoryTab';
import { PullsTab } from './PullsTab';

type Sub = 'code' | 'history' | 'pulls';

export function RepoView({ boardId, onConnectGitHub }: { boardId: string; onConnectGitHub: () => void }) {
  const [sub, setSub] = useState<Sub>('code');
  const [meta, setMeta] = useState<RepoMeta | null>(null);
  const [branch, setBranch] = useState<string>('');
  const [branches, setBranches] = useState<string[]>([]);
  const [notConnected, setNotConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setError(null); setNotConnected(false);
    (async () => {
      try {
        const m = await api.repo.meta(boardId);
        if (!live) return;
        setMeta(m); setBranch(b => b || m.defaultBranch);
        const bs = await api.repo.branches(boardId);
        if (!live) return;
        setBranches(bs.map(x => x.name));
      } catch (e) {
        if (!live) return;
        if (e instanceof ApiError && e.status === 409) setNotConnected(true);
        else setError(e instanceof Error ? e.message : 'Failed to load repository.');
      }
    })();
    return () => { live = false; };
  }, [boardId]);

  if (notConnected) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-3 text-[var(--muted)]">
        <Icon.warning size={32} />
        <p className="text-sm">No GitHub repository is connected to this board.</p>
        <button onClick={onConnectGitHub} className="px-3 py-1.5 rounded-md bg-[var(--primary)] text-white text-[13px] font-medium">Connect a GitHub repo</button>
      </div>
    );
  }
  if (error) {
    return <div className="flex flex-col items-center justify-center h-full gap-2 text-[var(--muted)]"><Icon.warning size={28} /><p className="text-sm">{error}</p></div>;
  }
  if (!meta) {
    return <div className="flex items-center justify-center h-full text-[var(--muted)] text-sm">Loading repository…</div>;
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-4 px-4 py-2 border-b border-[var(--border)]">
        {(['code', 'history', 'pulls'] as Sub[]).map(s => (
          <button key={s} onClick={() => setSub(s)}
            className={`text-[13px] font-medium border-b-2 -mb-2.5 pb-2 ${sub === s ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
            {s === 'code' ? 'Code' : s === 'history' ? 'History' : 'Pull Requests'}
          </button>
        ))}
        <div className="flex-1" />
        <select value={branch} onChange={e => setBranch(e.target.value)}
          className="text-xs bg-[var(--surface)] rounded px-2 py-1 outline-none border border-[var(--border)] text-[var(--foreground)]">
          {branches.length === 0 && <option value={branch}>{branch}</option>}
          {branches.map(b => <option key={b} value={b}>{b}</option>)}
        </select>
      </div>
      <div className="flex-1 min-h-0">
        {sub === 'code' && <CodeTab boardId={boardId} branch={branch} />}
        {sub === 'history' && <HistoryTab boardId={boardId} branch={branch} />}
        {sub === 'pulls' && <PullsTab boardId={boardId} />}
      </div>
    </div>
  );
}
```

(`CodeTab`/`HistoryTab`/`PullsTab` are created in Tasks 4–5. To keep this task building on its own, also create minimal placeholder versions of those three files now — each `'use client'` exporting a component that renders `null` — then Tasks 4–5 replace them. Create:
`CodeTab.tsx`, `HistoryTab.tsx`, `PullsTab.tsx` each as:
```tsx
'use client';
export function CodeTab(_: { boardId: string; branch: string }) { return null; }
```
adjusting the export name + props per file: `HistoryTab({ boardId, branch })`, `PullsTab({ boardId })`.)

- [ ] **Step 2: Add the pinned Repo tab to `ViewTabs`.** In `src/web/tectika-board/src/components/board/ViewTabs.tsx`, change the `ViewTabs` function signature to accept optional props and render a pinned Repo tab + notify on view selection. Replace `export function ViewTabs() {` with:

```tsx
export function ViewTabs({ repoActive = false, onRepoClick, onViewSelect }: { repoActive?: boolean; onRepoClick?: () => void; onViewSelect?: () => void } = {}) {
```
In the same component, find the per-view `setActiveView(v.id)` onClick (inside the `views.map`) and change that tab's click to also call `onViewSelect?.()`. The simplest: where it renders `<ViewTab ... onClick={() => setActiveView(v.id)} />`, change to `onClick={() => { onViewSelect?.(); setActiveView(v.id); }}`. Then, immediately before the closing `</div>` of the root flex row (after the "add view" button/menu), add the pinned Repo tab:

```tsx
      {onRepoClick && (
        <>
          <div className="w-px self-center h-5 bg-[var(--border)] mx-1 shrink-0" />
          <button onClick={onRepoClick}
            className={`flex items-center gap-1.5 px-3 py-2.5 text-[13px] font-medium border-b-2 -mb-px shrink-0 transition-colors ${repoActive ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
            <Icon.file size={15} /> Repo
          </button>
        </>
      )}
```
(`Icon.file` is already imported in this file — it uses `Icon` already. If not, add `import { Icon } from '@/components/ui/icons';` — check the existing imports first.)

- [ ] **Step 3: Wire `BoardView`.** In `src/web/tectika-board/src/components/board/BoardView.tsx`:

(a) Add the import near the other component imports:
```tsx
import { RepoView } from '@/components/board/repo/RepoView';
```
(b) Add state near the other `useState` calls in `BoardView`:
```tsx
  const [showRepo, setShowRepo] = useState(false);
```
(c) Replace `<ViewTabs />` with:
```tsx
      <ViewTabs repoActive={showRepo} onRepoClick={() => setShowRepo(true)} onViewSelect={() => setShowRepo(false)} />
```
(d) Replace the body block:
```tsx
      <div className="flex-1 min-h-0 relative">
        {loading ? <SkeletonRows rows={8} /> : tasks.length === 0 ? (
          <EmptyState icon={<Icon.board size={48} />} title="This board is empty"
            description="Add your first item to start orchestrating agents and humans."
            action={<Button variant="primary" onClick={() => addTask({ title: 'New item' })}><Icon.plus size={16} /> Add item</Button>} />
        ) : (
          <ActiveView kind={activeView.kind} />
        )}
        <BatchToolbar />
      </div>
```
with (render `RepoView` when `showRepo`, otherwise the existing content; also hide the `Toolbar` while in repo mode):
```tsx
      <div className="flex-1 min-h-0 relative">
        {showRepo ? (
          <RepoView boardId={board!.id} onConnectGitHub={() => setGithubOpen(true)} />
        ) : loading ? <SkeletonRows rows={8} /> : tasks.length === 0 ? (
          <EmptyState icon={<Icon.board size={48} />} title="This board is empty"
            description="Add your first item to start orchestrating agents and humans."
            action={<Button variant="primary" onClick={() => addTask({ title: 'New item' })}><Icon.plus size={16} /> Add item</Button>} />
        ) : (
          <ActiveView kind={activeView.kind} />
        )}
        <BatchToolbar />
      </div>
```
Also move the `<Toolbar />` so it only renders when not in repo mode: change the line `<Toolbar />` to `{!showRepo && <Toolbar />}`.
(`board!` is safe here — this block only renders after the loading/error guards where `board` is set; if the compiler complains, guard with `board && (...)` around the `RepoView`.)

- [ ] **Step 4: Verify.** Run from `src/web/tectika-board`:
  - `npx tsc --noEmit` → no errors
  - `npm run lint` → no NEW errors vs the baseline (currently 7 errors / 6 warnings, all pre-existing)
  - `npm run build` → succeeds

- [ ] **Step 5: Commit:**
```bash
git add src/web/tectika-board/src/components/board/repo/ src/web/tectika-board/src/components/board/ViewTabs.tsx src/web/tectika-board/src/components/board/BoardView.tsx
git commit -m "feat(web): RepoView shell + pinned Repo tab wired into the board"
```

---

## Task 4: Code sub-tab (file tree + file viewer)

**Files:**
- Modify (replace placeholder): `src/web/tectika-board/src/components/board/repo/CodeTab.tsx`

- [ ] **Step 1: Implement `CodeTab`.** Replace `src/web/tectika-board/src/components/board/repo/CodeTab.tsx` with:

```tsx
'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { TreeEntry, FileContent } from '@/lib/types';
import { Icon } from '@/components/ui/icons';
import { languageForPath, highlightToHtml } from '@/lib/highlight';

export function CodeTab({ boardId, branch }: { boardId: string; branch: string }) {
  const [entries, setEntries] = useState<TreeEntry[]>([]);
  const [dir, setDir] = useState('');             // current directory path ('' = root)
  const [selected, setSelected] = useState<string | null>(null);
  const [loadingTree, setLoadingTree] = useState(true);

  useEffect(() => {
    let live = true;
    setLoadingTree(true);
    api.repo.tree(boardId, branch, dir)
      .then(e => { if (live) setEntries(e); })
      .catch(() => { if (live) setEntries([]); })
      .finally(() => { if (live) setLoadingTree(false); });
    return () => { live = false; };
  }, [boardId, branch, dir]);

  const up = () => setDir(d => d.includes('/') ? d.slice(0, d.lastIndexOf('/')) : '');

  return (
    <div className="flex h-full">
      <div className="w-1/3 max-w-[320px] border-r border-[var(--border)] overflow-auto p-2 text-[13px]">
        {dir !== '' && (
          <button onClick={up} className="flex items-center gap-1 text-[var(--muted)] hover:text-[var(--foreground)] px-1 py-0.5"><Icon.chevronLeft size={14} /> ..</button>
        )}
        {loadingTree ? <div className="text-[var(--muted)] p-2">Loading…</div>
          : entries.length === 0 ? <div className="text-[var(--muted)] p-2">Empty.</div>
          : entries.map(e => (
            <button key={e.path} onClick={() => e.type === 'dir' ? setDir(e.path) : setSelected(e.path)}
              className={`flex items-center gap-1.5 w-full text-left px-1.5 py-1 rounded hover:bg-[var(--surface)] ${selected === e.path ? 'bg-[var(--surface)] text-[var(--primary)]' : 'text-[var(--foreground)]'}`}>
              <Icon.file size={14} className={e.type === 'dir' ? 'text-[var(--primary)]' : 'text-[var(--muted)]'} />
              <span className="truncate">{e.name}{e.type === 'dir' ? '/' : ''}</span>
            </button>
          ))}
      </div>
      <div className="flex-1 min-w-0 overflow-auto">
        {selected ? <FileViewer boardId={boardId} branch={branch} path={selected} /> : <div className="flex items-center justify-center h-full text-[var(--muted)] text-sm">Select a file to view.</div>}
      </div>
    </div>
  );
}

function FileViewer({ boardId, branch, path }: { boardId: string; branch: string; path: string }) {
  const [file, setFile] = useState<FileContent | null>(null);
  const [html, setHtml] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let live = true;
    setLoading(true); setHtml(null); setFile(null);
    api.repo.file(boardId, path, branch)
      .then(async f => {
        if (!live) return;
        setFile(f);
        if (!f.isBinary && f.text != null) {
          const h = await highlightToHtml(f.text, languageForPath(f.path));
          if (live) setHtml(h);
        }
      })
      .catch(() => { if (live) setFile(null); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [boardId, branch, path]);

  if (loading) return <div className="p-4 text-[var(--muted)] text-sm">Loading file…</div>;
  if (!file) return <div className="p-4 text-[var(--muted)] text-sm">Could not load this file.</div>;
  if (file.isBinary || file.text == null) {
    return <div className="p-4 text-[var(--muted)] text-sm">Binary or large file ({file.size} bytes) — not shown.</div>;
  }
  return (
    <div className="text-[12.5px]">
      <div className="px-3 py-1.5 border-b border-[var(--border)] text-[11px] text-[var(--muted)] font-mono">{file.path}</div>
      {html
        ? <div className="repo-code [&_pre]:!m-0 [&_pre]:!p-3 [&_pre]:overflow-auto [&_pre]:text-[12.5px]" dangerouslySetInnerHTML={{ __html: html }} />
        : <pre className="font-mono p-3 overflow-auto whitespace-pre text-[var(--foreground)]">{file.text}</pre>}
    </div>
  );
}
```

- [ ] **Step 2: Verify.** From `src/web/tectika-board`: `npx tsc --noEmit` (0 errors), `npm run lint` (no new), `npm run build` (succeeds).

- [ ] **Step 3: Commit:**
```bash
git add src/web/tectika-board/src/components/board/repo/CodeTab.tsx
git commit -m "feat(web): Code tab — file tree + Shiki file viewer with binary fallback"
```

---

## Task 5: History + Pull Requests sub-tabs

**Files:**
- Modify (replace placeholders): `src/web/tectika-board/src/components/board/repo/HistoryTab.tsx`, `src/web/tectika-board/src/components/board/repo/PullsTab.tsx`

- [ ] **Step 1: Implement `HistoryTab`.** Replace `src/web/tectika-board/src/components/board/repo/HistoryTab.tsx` with:

```tsx
'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { CommitInfo } from '@/lib/types';

export function HistoryTab({ boardId, branch }: { boardId: string; branch: string }) {
  const [commits, setCommits] = useState<CommitInfo[] | null>(null);

  useEffect(() => {
    let live = true;
    setCommits(null);
    api.repo.commits(boardId, branch).then(c => { if (live) setCommits(c); }).catch(() => { if (live) setCommits([]); });
    return () => { live = false; };
  }, [boardId, branch]);

  if (commits === null) return <div className="p-4 text-[var(--muted)] text-sm">Loading history…</div>;
  if (commits.length === 0) return <div className="p-4 text-[var(--muted)] text-sm">No commits on this branch.</div>;
  return (
    <div className="overflow-auto h-full divide-y divide-[var(--border)]">
      {commits.map(c => (
        <a key={c.sha} href={c.url} target="_blank" rel="noreferrer" className="block px-4 py-2.5 hover:bg-[var(--surface)]">
          <div className="text-[13px] text-[var(--foreground)] truncate">{c.message.split('\n')[0]}</div>
          <div className="text-[11px] text-[var(--muted)] font-mono">{c.sha.slice(0, 7)} · {c.author} · {new Date(c.date).toLocaleDateString()}</div>
        </a>
      ))}
    </div>
  );
}
```

- [ ] **Step 2: Implement `PullsTab`.** Replace `src/web/tectika-board/src/components/board/repo/PullsTab.tsx` with:

```tsx
'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { PullRequestInfo } from '@/lib/types';

export function PullsTab({ boardId }: { boardId: string }) {
  const [state, setState] = useState<'open' | 'closed' | 'all'>('open');
  const [prs, setPrs] = useState<PullRequestInfo[] | null>(null);

  useEffect(() => {
    let live = true;
    setPrs(null);
    api.repo.pulls(boardId, state).then(p => { if (live) setPrs(p); }).catch(() => { if (live) setPrs([]); });
    return () => { live = false; };
  }, [boardId, state]);

  return (
    <div className="flex flex-col h-full">
      <div className="flex gap-2 px-4 py-2 border-b border-[var(--border)]">
        {(['open', 'closed', 'all'] as const).map(s => (
          <button key={s} onClick={() => setState(s)}
            className={`text-[12px] px-2 py-0.5 rounded capitalize ${state === s ? 'bg-[var(--primary)] text-white' : 'text-[var(--muted)] hover:text-[var(--foreground)]'}`}>{s}</button>
        ))}
      </div>
      <div className="flex-1 overflow-auto divide-y divide-[var(--border)]">
        {prs === null ? <div className="p-4 text-[var(--muted)] text-sm">Loading pull requests…</div>
          : prs.length === 0 ? <div className="p-4 text-[var(--muted)] text-sm">No {state} pull requests.</div>
          : prs.map(pr => (
            <a key={pr.number} href={pr.url} target="_blank" rel="noreferrer" className="block px-4 py-2.5 hover:bg-[var(--surface)]">
              <div className="text-[13px] text-[var(--foreground)] truncate">#{pr.number} {pr.title}</div>
              <div className="text-[11px] text-[var(--muted)] font-mono">{pr.state} · {pr.head} → {pr.base} · {pr.author}</div>
            </a>
          ))}
      </div>
    </div>
  );
}
```

- [ ] **Step 3: Verify.** From `src/web/tectika-board`: `npx tsc --noEmit` (0 errors), `npm run lint` (no new), `npm run build` (succeeds).

- [ ] **Step 4: Manual render check.** Start the app + mock DB per the project's AgentBoard QA flow. On a board **with** a connected GitHub repo: click the **Repo** tab → file tree loads on the default branch; click a folder → navigates in; click a file → it renders highlighted; switch branch → tree reloads; History tab → commits list; Pull Requests tab → PRs with open/closed/all filter. On a board **without** GitHub: the Repo tab shows the "Connect a GitHub repo" prompt wired to the connect modal. Confirm no console errors.

- [ ] **Step 5: Commit:**
```bash
git add src/web/tectika-board/src/components/board/repo/HistoryTab.tsx src/web/tectika-board/src/components/board/repo/PullsTab.tsx
git commit -m "feat(web): History (commits) + Pull Requests sub-tabs in the repo view"
```

---

## Self-Review

**Spec coverage (against `2026-06-17-code-viewer-design.md` §4.4, §6):**
- Pinned Repo tab in the view row, own component, not the view/filter machinery → Task 3 (ViewTabs + BoardView). ✓
- Code (tree + Shiki viewer), History (commits), Pull Requests (open/closed/all) + branch switcher → Tasks 3, 4, 5. ✓
- No-GitHub → connect prompt wired to `GitHubConnectModal` (via `onConnectGitHub` → `setGithubOpen`) → Task 3. ✓
- Binary/large file → "not shown" fallback (`isBinary`/`text==null`) → Task 4. ✓
- Empty/error/loading states for tree, file, commits, PRs → Tasks 3–5. ✓
- Shiki highlighting + extension→language map → Task 2. ✓
- **Out of scope** (diffs, Code outputs, live preview) → not present. ✓

**Placeholder scan:** Components have full code. Task 3 creates real placeholder `CodeTab`/`HistoryTab`/`PullsTab` (render `null`) so it builds independently, then Tasks 4–5 replace them — this is sequenced, not a placeholder-in-final-code. Task 2 Step 3 flags the repo's `node --test` TS mechanism as a thing to match (the existing `*.test.ts` convention) — necessary, not vague.

**Type consistency:** `RepoMeta`/`BranchInfo`/`TreeEntry`/`FileContent`/`CommitInfo`/`PullRequestInfo` (Task 1) match their C# DTO origins (camelCase) and are used identically in `api.repo` (Task 1) and the components (Tasks 3–5). `languageForPath`/`highlightToHtml` (Task 2) signatures match their use in `CodeTab` (Task 4). `RepoView` props (`boardId`, `onConnectGitHub`) match the `BoardView` call site (Task 3). `ViewTabs` new optional props (`repoActive`/`onRepoClick`/`onViewSelect`) match the `BoardView` call site.
