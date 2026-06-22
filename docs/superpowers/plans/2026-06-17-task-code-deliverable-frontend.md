# Task-level Code Deliverable — Plan 3B (Frontend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render a task's automatic Code output as a compact card in the task pane, and a full diff in a new board **Repo → Changes** surface that the card deep-links into.

**Architecture:** The `OutputView` Code branch becomes a card reading `external.locator`; its "Open diff" calls a new `board-context` `openRepoChanges(head)` signal and closes the task panel. `BoardView` reacts by opening the Repo tab; `RepoView` opens a new **Changes** sub-tab that fetches `api.repo.compare(base, head)` and renders per-file diffs via a pure `parseUnifiedDiff` helper.

**Tech Stack:** Next.js 16 / React 19 / TypeScript / Tailwind v4, `node --test` for the pure diff parser.

**Builds on:** Plan 3A (same branch `feat/code-outputs`) — the `/repo/compare` endpoint, the `Code` output with its `Dictionary<string,string>` locator (keys: `owner, repo, branch, base, headSha, filesChanged, additions, deletions, prNumber?, prUrl?`; numbers stringified; `prNumber`/`prUrl` absent when no PR; binary files arrive `isBinary:true, patch:null`). Completes Spec 3.

**Testing reality:** no component-test runner; pure helpers use `node --test`, components verified by `npx tsc --noEmit` + `npm run lint` (no new errors vs baseline) + `npm run build` + a manual render check. All work under `src/web/tectika-board`.

---

## File Structure

**Create:**
- `src/web/tectika-board/src/lib/diff.ts` — pure `parseUnifiedDiff(patch)` → diff lines.
- `src/web/tectika-board/src/lib/diff.test.ts` — `node --test`.
- `src/web/tectika-board/src/components/board/repo/ChangesTab.tsx` — file list + per-file diff (compare).

**Modify:**
- `src/web/tectika-board/src/lib/types.ts` — `CompareResult`/`DiffFile`.
- `src/web/tectika-board/src/lib/api.ts` — `api.repo.compare`.
- `src/web/tectika-board/src/lib/board-context.tsx` — `repoChangesTarget` + `openRepoChanges`.
- `src/web/tectika-board/src/components/board/BoardView.tsx` — react to `repoChangesTarget`.
- `src/web/tectika-board/src/components/board/repo/RepoView.tsx` — `Changes` sub-tab + `changesTarget` prop.
- `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` — the `OutputView` Code card.

---

## Task 1: compare types + client

**Files:** Modify `src/web/tectika-board/src/lib/types.ts`, `src/web/tectika-board/src/lib/api.ts`

- [ ] **Step 1: Types.** In `types.ts`, after the existing repo DTOs (added in Spec 2), append:
```typescript
export interface DiffFile { path: string; status: string; additions: number; deletions: number; isBinary: boolean; patch: string | null; }
export interface CompareResult { headSha: string; filesChanged: number; additions: number; deletions: number; files: DiffFile[]; }
```

- [ ] **Step 2: Client.** In `api.ts`, add `CompareResult` to the `import type { ... } from './types'` list, then add to the `repo` group (after `pulls`):
```typescript
    compare: (boardId: string, base: string, head: string) =>
      fetchApi<CompareResult>(`/api/boards/${boardId}/repo/compare?base=${encodeURIComponent(base)}&head=${encodeURIComponent(head)}`),
```

- [ ] **Step 3: Type-check.** `cd src/web/tectika-board && npx tsc --noEmit` → 0 errors.

- [ ] **Step 4: Commit:**
```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts
git commit -m "feat(web): compare types + api.repo.compare client"
```

---

## Task 2: `parseUnifiedDiff` pure helper

**Files:** Create `src/web/tectika-board/src/lib/diff.ts`, `src/web/tectika-board/src/lib/diff.test.ts`

- [ ] **Step 1: Write the failing test.** Create `src/web/tectika-board/src/lib/diff.test.ts` (match how the repo's other `*.test.ts` run under `node --test` — e.g. `src/lib/highlight.test.ts` from Spec 2; mirror its import/run style):
```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseUnifiedDiff } from './diff.ts';

test('classifies add / del / context / hunk lines', () => {
  const patch = '@@ -1,2 +1,2 @@\n context\n-old\n+new';
  const lines = parseUnifiedDiff(patch);
  assert.equal(lines[0].type, 'hunk');
  assert.equal(lines[1].type, 'context');
  assert.equal(lines[2].type, 'del');
  assert.equal(lines[2].text, 'old');
  assert.equal(lines[3].type, 'add');
  assert.equal(lines[3].text, 'new');
});

test('empty / null patch → empty array', () => {
  assert.deepEqual(parseUnifiedDiff(''), []);
  assert.deepEqual(parseUnifiedDiff(null), []);
});

test('strips the leading +/-/space marker from text', () => {
  const lines = parseUnifiedDiff('+added line');
  assert.equal(lines[0].type, 'add');
  assert.equal(lines[0].text, 'added line');
});
```

- [ ] **Step 2: Run, verify FAIL** — `cd src/web/tectika-board && node --test src/lib/diff.test.ts` (use the same `.ts` test mechanism the repo's existing tests use; if they use a flag like `--experimental-strip-types`, match it).

- [ ] **Step 3: Implement.** Create `src/web/tectika-board/src/lib/diff.ts`:
```typescript
export type DiffLineType = 'add' | 'del' | 'context' | 'hunk';
export interface DiffLine { type: DiffLineType; text: string; }

/** Parse a unified-diff patch (GitHub's per-file `patch`) into typed lines.
 * Null/empty patch → []. The leading +/-/space marker is stripped from `text`;
 * hunk headers (@@) are kept verbatim. */
export function parseUnifiedDiff(patch: string | null | undefined): DiffLine[] {
  if (!patch) return [];
  return patch.split('\n').map(line => {
    if (line.startsWith('@@')) return { type: 'hunk' as const, text: line };
    if (line.startsWith('+')) return { type: 'add' as const, text: line.slice(1) };
    if (line.startsWith('-')) return { type: 'del' as const, text: line.slice(1) };
    return { type: 'context' as const, text: line.startsWith(' ') ? line.slice(1) : line };
  });
}
```

- [ ] **Step 4: Run, verify PASS** — `cd src/web/tectika-board && node --test src/lib/diff.test.ts` → 3 pass.

- [ ] **Step 5: Commit:**
```bash
git add src/web/tectika-board/src/lib/diff.ts src/web/tectika-board/src/lib/diff.test.ts
git commit -m "feat(web): parseUnifiedDiff helper (tested)"
```

---

## Task 3: `board-context` openRepoChanges signal

**Files:** Modify `src/web/tectika-board/src/lib/board-context.tsx`

No unit test (context plumbing; verified by tsc + consumers in Tasks 4–5).

- [ ] **Step 1: Add to the context type.** In `board-context.tsx`, in the `interface BoardContextValue { ... }`, after the `openTask` line, add:
```typescript
  repoChangesTarget?: string;            // head branch to open in the Repo "Changes" tab
  openRepoChanges: (head: string) => void;
  clearRepoChangesTarget: () => void;
```

- [ ] **Step 2: Add state.** Near the other `useState` calls in the provider (e.g. by `openTaskId`), add:
```typescript
  const [repoChangesTarget, setRepoChangesTarget] = useState<string | undefined>();
```

- [ ] **Step 3: Expose in the value.** In the `const value: BoardContextValue = { ... }` object (near `openTaskId, openTask: setOpenTaskId,`), add:
```typescript
    repoChangesTarget,
    openRepoChanges: setRepoChangesTarget,
    clearRepoChangesTarget: () => setRepoChangesTarget(undefined),
```

- [ ] **Step 4: Type-check.** `cd src/web/tectika-board && npx tsc --noEmit` → 0 errors.

- [ ] **Step 5: Commit:**
```bash
git add src/web/tectika-board/src/lib/board-context.tsx
git commit -m "feat(web): board-context openRepoChanges signal for task->repo diff jump"
```

---

## Task 4: Code card in `OutputView`

**Files:** Modify `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`

- [ ] **Step 1: Replace the `OutputView` Code placeholder.** In `ItemPanel.tsx`, replace the entire `OutputView` function with one that renders a Code card for `kind === 'Code'` and keeps the Document branch + the generic placeholder for other kinds:
```tsx
function OutputView({ output }: { output: Output }) {
  if (output.kind === 'Document' && output.inline) {
    return output.inline.contentType === 'Markdown'
      ? <Markdown text={output.inline.content} />
      : <pre className="font-mono text-[12.5px] bg-[var(--background)] border border-[var(--border)] rounded-lg p-3 overflow-auto whitespace-pre-wrap text-[var(--foreground)]">{output.inline.content}</pre>;
  }
  if (output.kind === 'Code' && output.external) {
    return <CodeOutputCard output={output} />;
  }
  return (
    <div className="border border-dashed border-[var(--border)] rounded-lg p-3 text-[12px] text-[var(--muted)]">
      <span className="font-semibold text-[var(--foreground)]">{output.label ?? output.kind}</span>{' — '}{output.kind} output rendering coming soon.
    </div>
  );
}

function CodeOutputCard({ output }: { output: Output }) {
  const { openRepoChanges, openTask } = useBoard();
  const loc = output.external!.locator as Record<string, string>;
  const branch = loc.branch ?? '';
  const base = loc.base ?? 'main';
  const files = loc.filesChanged ?? '0';
  const adds = loc.additions ?? '0';
  const dels = loc.deletions ?? '0';
  const prNumber = loc.prNumber;
  const prUrl = loc.prUrl;

  return (
    <div className="border border-[var(--border)] rounded-lg p-3">
      <div className="flex items-center gap-2 mb-1">
        <Icon.code size={14} className="text-[var(--muted)]" />
        <span className="font-semibold text-[13px] text-[var(--foreground)]">{output.label ?? 'Code'}</span>
        {prUrl && <a href={prUrl} target="_blank" rel="noreferrer" className="ml-auto text-[12px] text-[var(--primary)] hover:underline">PR #{prNumber}</a>}
      </div>
      <div className="text-[11px] text-[var(--muted)] font-mono">{branch} · vs {base} · {files} files · <span className="text-emerald-500">+{adds}</span> <span className="text-red-500">−{dels}</span></div>
      <button
        onClick={() => { openRepoChanges(branch); openTask(undefined); }}
        className="mt-2 px-2.5 py-1 rounded-md bg-[var(--primary)] text-white text-[12px] font-medium">
        Open diff →
      </button>
    </div>
  );
}
```

- [ ] **Step 2: Imports.** Ensure `useBoard` and `Icon` are imported in `ItemPanel.tsx` (they already are — `useBoard` is used at the top and `Icon` throughout; if `Icon.code` doesn't exist, use `Icon.file`). Confirm `Output` is imported (it is, from Spec 1's frontend work).

- [ ] **Step 3: Verify.** `cd src/web/tectika-board && npx tsc --noEmit` (0 errors), `npm run lint` (no new), `npm run build` (succeeds).

- [ ] **Step 4: Commit:**
```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): Code output card in the task pane (open diff -> board Repo)"
```

---

## Task 5: Repo "Changes" sub-tab + diff view + BoardView wiring

**Files:**
- Create: `src/web/tectika-board/src/components/board/repo/ChangesTab.tsx`
- Modify: `src/web/tectika-board/src/components/board/repo/RepoView.tsx`, `src/web/tectika-board/src/components/board/BoardView.tsx`

- [ ] **Step 1: Create `ChangesTab`.** Create `src/web/tectika-board/src/components/board/repo/ChangesTab.tsx`:
```tsx
'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { CompareResult, DiffFile } from '@/lib/types';
import { parseUnifiedDiff } from '@/lib/diff';

export function ChangesTab({ boardId, base, head }: { boardId: string; base: string; head: string }) {
  const [result, setResult] = useState<CompareResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset on base/head change
    setResult(null); setError(null); setSelected(null);
    api.repo.compare(boardId, base, head)
      .then(r => { if (live) { setResult(r); setSelected(r.files[0]?.path ?? null); } })
      .catch(() => { if (live) setError('Could not load the diff for this branch.'); });
    return () => { live = false; };
  }, [boardId, base, head]);

  if (error) return <div className="p-4 text-[var(--muted)] text-sm">{error}</div>;
  if (!result) return <div className="p-4 text-[var(--muted)] text-sm">Loading changes…</div>;
  if (result.filesChanged === 0) return <div className="p-4 text-[var(--muted)] text-sm">No changes between {base} and {head}.</div>;

  const file = result.files.find(f => f.path === selected) ?? null;
  return (
    <div className="flex h-full">
      <div className="w-1/3 max-w-[320px] border-r border-[var(--border)] overflow-auto p-2 text-[12px]">
        <div className="px-1 py-1 text-[11px] text-[var(--muted)] font-mono">{head} vs {base} · {result.filesChanged} files</div>
        {result.files.map(f => (
          <button key={f.path} onClick={() => setSelected(f.path)}
            className={`flex items-center gap-2 w-full text-left px-1.5 py-1 rounded hover:bg-[var(--surface)] ${selected === f.path ? 'bg-[var(--surface)] text-[var(--primary)]' : 'text-[var(--foreground)]'}`}>
            <span className="truncate flex-1 font-mono">{f.path}</span>
            <span className="text-emerald-500 text-[11px]">+{f.additions}</span>
            <span className="text-red-500 text-[11px]">−{f.deletions}</span>
          </button>
        ))}
      </div>
      <div className="flex-1 min-w-0 overflow-auto">
        {file ? <DiffView file={file} /> : <div className="flex items-center justify-center h-full text-[var(--muted)] text-sm">Select a file.</div>}
      </div>
    </div>
  );
}

function DiffView({ file }: { file: DiffFile }) {
  if (file.isBinary || file.patch == null) {
    return <div className="p-4 text-[var(--muted)] text-sm">Binary file — not shown.</div>;
  }
  const lines = parseUnifiedDiff(file.patch);
  return (
    <div>
      <div className="px-3 py-1.5 border-b border-[var(--border)] text-[11px] text-[var(--muted)] font-mono">{file.path}</div>
      <pre className="font-mono text-[12px] overflow-auto">
        {lines.map((l, i) => (
          <div key={i} className={
            l.type === 'add' ? 'bg-emerald-500/10 text-emerald-300' :
            l.type === 'del' ? 'bg-red-500/10 text-red-300' :
            l.type === 'hunk' ? 'text-[var(--muted)] bg-[var(--surface)]' : 'text-[var(--foreground)]'}>
            <span className="px-3">{l.type === 'add' ? '+' : l.type === 'del' ? '−' : ' '}{l.text}</span>
          </div>
        ))}
      </pre>
    </div>
  );
}
```

- [ ] **Step 2: Add the Changes sub-tab to `RepoView`.** In `src/web/tectika-board/src/components/board/repo/RepoView.tsx`:
- Add the import: `import { ChangesTab } from './ChangesTab';`
- Add `'changes'` to the `Sub` type: `type Sub = 'code' | 'history' | 'pulls' | 'changes';`
- Accept a prop: change the component signature to `export function RepoView({ boardId, onConnectGitHub, changesTarget }: { boardId: string; onConnectGitHub: () => void; changesTarget?: string })`.
- Track the changes head in state (near the other `useState`s): `const [changesHead, setChangesHead] = useState<string | undefined>();`
- Add an effect (after the existing meta effect) to react to a deep-link target:
```tsx
  useEffect(() => {
    if (changesTarget) { setChangesHead(changesTarget); setSub('changes'); }
  }, [changesTarget]);
```
- In the sub-tab button row, render a **Changes** tab only when there's a head to show. Where the existing buttons map over `['code','history','pulls']`, leave those as-is and add, right after that map:
```tsx
        {changesHead && (
          <button onClick={() => setSub('changes')}
            className={`text-[13px] font-medium border-b-2 -mb-2.5 pb-2 ${sub === 'changes' ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
            Changes
          </button>
        )}
```
- In the body switch (where `sub === 'code'` etc. render), add:
```tsx
        {sub === 'changes' && changesHead && <ChangesTab boardId={boardId} base={meta.defaultBranch} head={changesHead} />}
```

- [ ] **Step 3: Wire `BoardView`.** In `src/web/tectika-board/src/components/board/BoardView.tsx`:
- Pull the signal from context: add `repoChangesTarget, clearRepoChangesTarget` to the `useBoard()` destructure.
- Add an effect to open the Repo tab when a target arrives:
```tsx
  useEffect(() => {
    if (repoChangesTarget) setShowRepo(true);
  }, [repoChangesTarget]);
```
- Pass the target to `RepoView` (and clear it once consumed): change the `<RepoView .../>` render to:
```tsx
          <RepoView boardId={board!.id} onConnectGitHub={() => setGithubOpen(true)} changesTarget={repoChangesTarget} />
```
- After `RepoView` mounts with the target, clear it so re-opening Repo later doesn't force Changes again. Add to the same effect:
```tsx
  useEffect(() => {
    if (repoChangesTarget) { setShowRepo(true); const t = setTimeout(() => clearRepoChangesTarget(), 0); return () => clearTimeout(t); }
  }, [repoChangesTarget, clearRepoChangesTarget]);
```
(Replace the simpler effect above with this one — the `setTimeout(…,0)` lets `RepoView` capture the target via its own effect before it's cleared. `RepoView` keeps `changesHead` in its own state, so clearing the context target afterward is safe.)

- [ ] **Step 4: Verify.** `cd src/web/tectika-board && npx tsc --noEmit` (0 errors), `npm run lint` (no new vs baseline), `npm run build` (succeeds).

- [ ] **Step 5: Manual render check.** With a board whose repo has an agent branch + changes (or any two branches), open a task whose artifact has a Code output → the **Code card** shows branch/±/PR; click **Open diff** → the panel closes, the board switches to **Repo → Changes**, the changed-files list loads, clicking a file shows the colored diff; a binary file shows "Binary file — not shown". Confirm no console errors.

- [ ] **Step 6: Commit:**
```bash
git add src/web/tectika-board/src/components/board/repo/ChangesTab.tsx src/web/tectika-board/src/components/board/repo/RepoView.tsx src/web/tectika-board/src/components/board/BoardView.tsx
git commit -m "feat(web): Repo Changes sub-tab + diff view, wired from the Code card"
```

---

## Self-Review

**Spec coverage (against `2026-06-17-task-code-deliverable-design.md` §5.5):**
- Code card in the task pane (reads locator, ±, PR link, "Open diff") → Task 4. ✓
- `openRepoChanges(head)` board-context signal → Task 3; consumed by BoardView (open Repo) + RepoView (Changes) → Task 5. ✓
- Repo "Changes" sub-tab: `api.repo.compare(base, head)` → file list + per-file diff in the wide surface → Tasks 1, 5. ✓
- `parseUnifiedDiff` (pure, tested) + green/red rendering → Task 2, 5. ✓
- Binary/large file → "not shown" → Task 5 (`DiffView`). ✓
- Errors/empty/loading (compare fails / 0 files / loading) → Task 5 (`ChangesTab`). ✓
- **Out of scope** (live preview) → not present. ✓

**Placeholder scan:** Full code in every component step. The two "match the repo's `node --test` `.ts` mechanism" / "Icon.code may not exist → Icon.file" notes are necessary environment checks with the fallback given. No TODO/vague steps.

**Type consistency:** `CompareResult`/`DiffFile` (Task 1) used in `api.repo.compare` (Task 1) and `ChangesTab` (Task 5); `parseUnifiedDiff(patch)→DiffLine[]` (Task 2) used in `DiffView` (Task 5); `openRepoChanges`/`repoChangesTarget`/`clearRepoChangesTarget` (Task 3) match their use in `CodeOutputCard` (Task 4) + `BoardView`/`RepoView` (Task 5); the locator keys read in Task 4 (`branch, base, filesChanged, additions, deletions, prNumber, prUrl`) match what Plan 3A's `CodeOutputBuilder` writes; `RepoView` `changesTarget` prop matches the `BoardView` call site.
