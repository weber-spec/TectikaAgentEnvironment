# Live Chat Streaming + "Agent at Work" Experience — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the task chat show the agent's work live and never feel stuck, and keep the "working" state visible after navigating away and back.

**Architecture:** Frontend-only (spec Decision D3). The chat's running region becomes two layers — a permanent **history** built from the run events that already stream over SSE (rendered richly), and an ephemeral **live edge** (rotating context-seeded phrase + ticking timer + real token count) shown whenever the agent is working. "Working" is derived from the existing, server-synced `task.status === 'InProgress'` signal, which fixes the navigate-away bug for free.

**Tech Stack:** Next.js 16 (App Router, `'use client'`), React, Tailwind CSS + CSS custom properties in `globals.css`. No JS test runner in this app — verification is `tsc --noEmit` + `eslint` + `next build --webpack` + a live smoke.

**Spec:** `docs/superpowers/specs/2026-06-16-live-chat-streaming-design.md`

---

## File Structure

- **Create** `src/web/tectika-board/src/lib/thinking-phrases.ts` — pure module: phrase pools, `tool→context` map, and helpers (`pickPhrase`, `contextFromEvents`, `sumTokens`). No React.
- **Create** `src/web/tectika-board/src/components/workspace/LiveEdge.tsx` — the live-edge component (orb + shimmering phrase + elapsed timer + token count); manages its own rotation/timer intervals.
- **Modify** `src/web/tectika-board/src/app/globals.css` — add the live-edge keyframes + classes (following the existing `@keyframes` / `.animate-*` convention).
- **Modify** `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` — rework `AgentChat` into history layer + live edge; add `HistoryStep` + label helpers; derive `working` from `task.status`; drop the old thinking-dots block.

All paths below are relative to the repo root unless a `cd` is shown.

---

## Task 1: Live-edge CSS animations

**Files:**
- Modify: `src/web/tectika-board/src/app/globals.css` (append at end of file)

- [ ] **Step 1: Append the live-edge styles**

Append to the end of `src/web/tectika-board/src/app/globals.css`:

```css
/* ── Live edge: the "agent is working" treatment (chat) ──────────────────────── */
@keyframes liveedge-sweep {
  0%   { background-position: 130% 0; }
  100% { background-position: -130% 0; }
}
@keyframes liveedge-fade {
  from { opacity: 0.15; transform: translateY(2px); }
  to   { opacity: 1;    transform: none; }
}
@keyframes liveedge-spin { to { transform: rotate(360deg); } }
@keyframes liveedge-morph {
  0%, 100% { border-radius: 46% 54% 56% 44% / 52% 44% 56% 48%; }
  50%      { border-radius: 56% 44% 44% 56% / 44% 56% 48% 52%; }
}

.live-edge {
  background: var(--primary-light);
  border: 1px solid var(--border);
  border-radius: 12px;
}
.live-orb {
  width: 20px; height: 20px; flex: none; position: relative;
  background: conic-gradient(from 0deg, var(--primary), #a371ff, #37d39b, var(--primary));
  border-radius: 46% 54% 56% 44% / 52% 44% 56% 48%;
  animation: liveedge-spin 3.6s linear infinite, liveedge-morph 5s ease-in-out infinite;
  filter: blur(0.3px);
}
.live-orb::after {
  content: ''; position: absolute; inset: 5px; border-radius: 50%;
  background: var(--background);
}
.live-phrase {
  display: inline-block; max-width: 100%;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  font-size: 13px; font-weight: 600;
  background: linear-gradient(90deg,
    var(--muted-2) 0%, var(--muted-2) 38%, var(--foreground) 50%, var(--muted-2) 62%, var(--muted-2) 100%);
  background-size: 230% 100%;
  -webkit-background-clip: text; background-clip: text;
  color: transparent;
  animation: liveedge-sweep 2.4s linear infinite, liveedge-fade 0.4s ease;
}
@media (prefers-reduced-motion: reduce) {
  .live-orb, .live-phrase { animation: none; }
  .live-phrase { color: var(--muted); -webkit-text-fill-color: var(--muted); }
}
```

- [ ] **Step 2: Verify the CSS compiles in the build (deferred to Task 5)**

CSS has no standalone check; it is validated by `next build` in Task 5. Proceed.

- [ ] **Step 3: Commit**

```bash
git add src/web/tectika-board/src/app/globals.css
git commit -m "feat(web): live-edge animations (orb, phrase sweep)"
```

---

## Task 2: Phrase module (`thinking-phrases.ts`)

**Files:**
- Create: `src/web/tectika-board/src/lib/thinking-phrases.ts`

- [ ] **Step 1: Create the module**

Create `src/web/tectika-board/src/lib/thinking-phrases.ts`:

```ts
// Context-seeded "thinking" phrases for the chat live edge. Pure (no React).
// The live edge shows a rotating phrase so the agent never looks stuck during a
// long model call; the pool is chosen from what the agent most recently did.

import type { RunEvent } from './types';

export type PhraseContext = 'thinking' | 'exploring' | 'planning' | 'testing' | 'github';

export const PHRASE_POOLS: Record<PhraseContext, string[]> = {
  thinking: [
    'Percolating…', 'Untangling the threads…', 'Connecting the dots…', 'Mulling it over…',
    'Letting it simmer…', 'Thinking it through…', 'Chasing the idea…', 'Turning it over…',
  ],
  exploring: [
    'Reading the code…', 'Tracing the call path…', 'Getting the lay of the land…',
    'Following the imports…', 'Scanning for clues…', 'Mapping the board…',
  ],
  planning: [
    'Sketching the approach…', 'Weighing the options…', 'Shaping the change…',
    'Choosing sensible defaults…', 'Planning the next step…', 'Lining up the pieces…',
  ],
  testing: [
    'Running the suite…', 'Watching for red…', 'Checking edge cases…',
    'Letting the tests breathe…', 'Verifying nothing broke…',
  ],
  github: [
    'Talking to GitHub…', 'Syncing the branch…', 'Pushing the change…',
    'Opening the PR…', 'Wrangling git…',
  ],
};

/** Map a tool name to the phrase context it implies. Unknown → 'thinking'. */
export function toolContext(toolName: string | undefined | null): PhraseContext {
  if (!toolName) return 'thinking';
  const n = toolName.toLowerCase();
  if (n.includes('github') || n.includes('branch') || n.includes('_pr') || n.includes('pull_request') ||
      n.includes('push') || n.includes('commit') || n.includes('repo')) return 'github';
  if (n.includes('test')) return 'testing';
  if (n.includes('search') || n.includes('get_board') || n.includes('get_task') ||
      n.includes('get_artifact') || n.includes('read')) return 'exploring';
  if (n.includes('intent') || n.includes('brief') || n.includes('plan') || n.includes('write')) return 'planning';
  return 'thinking';
}

/** Pick a phrase from the pool, never repeating `last` back-to-back. */
export function pickPhrase(pool: string[], last: string | null): string {
  if (pool.length === 0) return '';
  if (pool.length === 1) return pool[0];
  let p = last;
  while (p === last) p = pool[Math.floor(Math.random() * pool.length)];
  return p;
}

/** Derive the live-edge context from the most recent tool the agent invoked. */
export function contextFromEvents(events: RunEvent[]): PhraseContext {
  let latest: RunEvent | undefined;
  for (const e of events) {
    if (e.kind === 'ToolCall' && e.toolName && (!latest || e.timestamp > latest.timestamp)) latest = e;
  }
  return latest ? toolContext(latest.toolName) : 'thinking';
}

/** Real cumulative tokens for the live edge — summed from events that carry usage. */
export function sumTokens(events: RunEvent[]): number {
  return events.reduce((sum, e) => sum + (e.tokenUsage?.total ?? 0), 0);
}
```

- [ ] **Step 2: Typecheck**

Run:
```bash
cd src/web/tectika-board && npx tsc --noEmit
```
Expected: no errors (exit 0).

- [ ] **Step 3: Lint the new file**

Run:
```bash
cd src/web/tectika-board && npx eslint src/lib/thinking-phrases.ts
```
Expected: no errors/warnings.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/thinking-phrases.ts
git commit -m "feat(web): context-seeded thinking phrases module"
```

---

## Task 3: Live-edge component (`LiveEdge.tsx`)

**Files:**
- Create: `src/web/tectika-board/src/components/workspace/LiveEdge.tsx`

- [ ] **Step 1: Create the component**

Create `src/web/tectika-board/src/components/workspace/LiveEdge.tsx`:

```tsx
'use client';

import { useEffect, useRef, useState } from 'react';
import { PHRASE_POOLS, pickPhrase, type PhraseContext } from '@/lib/thinking-phrases';

/**
 * The chat "agent is working" live edge: a presence orb + a shimmering,
 * context-seeded phrase that rotates every ~3s, an always-ticking elapsed timer,
 * and the real cumulative token count. Structurally never looks stuck because the
 * phrase + timer keep producing new information even when no event has arrived.
 */
export function LiveEdge({
  agentName, context, anchorAt, tokens,
}: {
  agentName?: string;
  context: PhraseContext;
  anchorAt?: string;   // ISO timestamp the elapsed timer counts from
  tokens: number;
}) {
  const [phrase, setPhrase] = useState(() => pickPhrase(PHRASE_POOLS[context], null));
  const lastRef = useRef(phrase);
  const [elapsed, setElapsed] = useState(0);

  // Rotate the phrase every 3s; swap immediately when the context changes.
  useEffect(() => {
    const swap = () => {
      const p = pickPhrase(PHRASE_POOLS[context], lastRef.current);
      lastRef.current = p;
      setPhrase(p);
    };
    swap();
    const id = setInterval(swap, 3000);
    return () => clearInterval(id);
  }, [context]);

  // Elapsed timer — smooth and always truthful.
  useEffect(() => {
    const start = anchorAt ? new Date(anchorAt).getTime() : Date.now();
    const tick = () => setElapsed(Math.max(0, Math.floor((Date.now() - start) / 1000)));
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [anchorAt]);

  const mm = Math.floor(elapsed / 60);
  const ss = String(elapsed % 60).padStart(2, '0');

  return (
    <div className="flex items-center gap-2.5 px-3 py-2.5 live-edge"
      role="status" aria-live="polite" aria-label={`${agentName ?? 'Agent'} is working`}>
      <span className="live-orb" aria-hidden />
      <div className="flex-1 min-w-0">
        {/* key={phrase} remounts the span so the fade-in replays on each swap */}
        <span key={phrase} className="live-phrase" aria-hidden>{phrase}</span>
        <div className="flex gap-2.5 mt-0.5 text-[10.5px] font-mono text-[var(--muted-2)]">
          <span>{mm}:{ss}</span>
          {tokens > 0 && <span className="text-[var(--muted)]">{tokens.toLocaleString()} tokens</span>}
        </div>
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Typecheck**

Run:
```bash
cd src/web/tectika-board && npx tsc --noEmit
```
Expected: no errors.

- [ ] **Step 3: Lint the new file**

Run:
```bash
cd src/web/tectika-board && npx eslint src/components/workspace/LiveEdge.tsx
```
Expected: no errors/warnings. (If `react-hooks/set-state-in-effect` fires on the `swap()`/`tick()` calls, add `// eslint-disable-next-line react-hooks/set-state-in-effect` immediately above the offending call — the same convention used in `ItemPanel.tsx`.)

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/LiveEdge.tsx
git commit -m "feat(web): LiveEdge component (rotating phrase + timer + tokens)"
```

---

## Task 4: Rework `AgentChat` into history layer + live edge

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`

This task replaces the `AgentChat` and `ChatBubble` functions, adds a `HistoryStep` component + label helpers, and adds two imports. Everything else in the file stays.

- [ ] **Step 1: Add imports**

In `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`, find the existing import of `RunTaskButton`:

```tsx
import { RunTaskButton } from '@/components/board/RunTaskButton';
```

Add these two lines immediately after it:

```tsx
import { LiveEdge } from './LiveEdge';
import { contextFromEvents, sumTokens } from '@/lib/thinking-phrases';
```

- [ ] **Step 2: Replace the `AgentChat` function**

Replace the entire `AgentChat` function (it currently begins at `function AgentChat({ task, role }: { task: AgentTask; role?: AgentRole }) {` and ends at the matching closing `}` before `function ChatBubble`) with:

```tsx
function AgentChat({ task, role }: { task: AgentTask; role?: AgentRole }) {
  const [activeRunId, setActiveRunId] = useState<string | undefined>(task.workflowRunId);
  const events = useRunEvents(task, activeRunId);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [pending, setPending] = useState<Bubble[]>([]);   // optimistic human turns (echo isn't streamed live)
  const [justSent, setJustSent] = useState(false);        // bridge until task.status syncs to InProgress
  const endRef = useRef<HTMLDivElement>(null);

  // Reset per-task UI state when switching tasks.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- clear pending bubbles on task change
  useEffect(() => { setPending([]); setActiveRunId(task.workflowRunId); setJustSent(false); }, [task.id]);

  // Once the server reflects the run (status → InProgress), task.status drives `working`.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- clear the optimistic bridge once status syncs
  useEffect(() => { if (task.status === 'InProgress') setJustSent(false); }, [task.status]);
  // Safety: never let the optimistic bridge linger if the run never started.
  useEffect(() => {
    if (!justSent) return;
    const id = setTimeout(() => setJustSent(false), 12000);
    return () => clearTimeout(id);
  }, [justSent]);

  // ── Chronological stream: user/agent bubbles + tool/artifact history steps ──
  type StreamItem =
    | { kind: 'user' | 'agent'; id: string; text: string; at: string }
    | { kind: 'step'; id: string; ev: RunEvent; at: string };

  const stream = useMemo<StreamItem[]>(() => {
    const items: StreamItem[] = [];
    for (const e of events) {
      if (e.kind === 'UserMessage') items.push({ kind: 'user', id: e.id, text: e.detail || e.title || '', at: e.timestamp });
      else if (e.kind === 'AgentMessage') items.push({ kind: 'agent', id: e.id, text: e.detail || e.title || '', at: e.timestamp });
      else if (e.kind === 'ToolCall' || e.kind === 'ArtifactWritten') items.push({ kind: 'step', id: e.id, ev: e, at: e.timestamp });
    }
    // optimistic human turns not yet present as a persisted UserMessage
    for (const p of pending) {
      if (!items.some(it => it.kind === 'user' && it.text === p.text)) items.push({ kind: 'user', id: p.id, text: p.text, at: p.at });
    }
    return items.sort((a, b) => +new Date(a.at) - +new Date(b.at));
  }, [events, pending]);

  // Timer anchor: the current turn's user message (or, for a board-triggered run, the run start).
  const anchorAt = useMemo(() => {
    const lastUser = events.filter(e => e.kind === 'UserMessage').map(e => e.timestamp).sort().at(-1);
    const fromPending = pending.at(-1)?.at;
    if (fromPending && (!lastUser || fromPending > lastUser)) return fromPending;
    if (lastUser) return lastUser;
    return events.map(e => e.timestamp).sort()[0];
  }, [events, pending]);

  // The agent has answered the current turn once a Final AgentMessage lands after the anchor.
  const answered = useMemo(
    () => !!anchorAt && events.some(e => e.kind === 'AgentMessage' && e.timestamp > anchorAt),
    [events, anchorAt],
  );

  // "Working" is server-derived (survives navigation): the task is InProgress, bridged by the
  // optimistic just-sent flag, and not yet answered for this turn.
  const working = (task.status === 'InProgress' || justSent || sending) && !answered;

  const liveContext = useMemo(() => contextFromEvents(events), [events]);
  const tokens = useMemo(() => sumTokens(events), [events]);

  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [stream.length, working]);

  const send = async () => {
    const text = draft.trim();
    if (!text || sending) return;
    const at = new Date().toISOString();
    setPending(p => [...p, { id: `local-${at}`, author: 'human', text, at }]);
    setDraft('');
    setSending(true);
    setJustSent(true);
    try { const res = await api.tasks.chat(task.boardId, task.id, text); setActiveRunId(res.runId); }
    catch { toast('Could not send message', 'error'); setJustSent(false); }
    finally { setSending(false); }
  };

  // ── Slash-command palette ──────────────────────────────────────────────────
  const { refreshTask } = useBoard();
  const slash = draft.startsWith('/');
  const cmdQuery = slash ? draft.slice(1) : '';
  const [cmdActive, setCmdActive] = useState(0);
  const [helpOpen, setHelpOpen] = useState(false);
  const cmdItems = useMemo(() => (slash ? filterCommands(cmdQuery) : []), [slash, cmdQuery]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- reset highlight as the command query changes
  useEffect(() => { setCmdActive(0); }, [cmdQuery]);
  const lastUserText = useMemo(() => {
    const echoed = events.filter(e => e.kind === 'UserMessage').map(e => e.detail || e.title || '');
    return pending.at(-1)?.text ?? echoed.at(-1);
  }, [events, pending]);
  const cmdCtx: ChatCommandContext = {
    boardId: task.boardId, taskId: task.id, isRunning: working, lastUserText,
    refreshTask: () => refreshTask(task.id),
    resend: (text) => { setDraft(''); api.tasks.chat(task.boardId, task.id, text).then(r => setActiveRunId(r.runId)).catch(() => toast('Could not resend', 'error')); },
    openHelp: () => setHelpOpen(true),
    toast,
  };
  const runCmd = (c: ChatCommand) => { if (c.enabled(cmdCtx)) { c.run(cmdCtx); setDraft(''); } };

  return (
    <div className="relative flex flex-col h-full min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-[var(--border)] text-[11px] text-[var(--muted)]">
        <span className="w-1.5 h-1.5 rounded-full bg-[#00c875]" />
        <span>{role ? role.displayName : 'Agent'} · {role?.modelOverride ?? 'default model'}</span>
        <span className="ml-auto text-[var(--muted-2)]">messages steer the run live</span>
      </div>
      <div className="flex-1 overflow-auto p-3 flex flex-col gap-2.5">
        {stream.length === 0 && !working && (
          <div className="text-center text-sm text-[var(--muted)] mt-8">
            <Icon.robot size={36} className="mx-auto mb-2 text-[var(--muted-2)]" />
            Start a conversation with this agent.<br />
            <span className="text-xs">Message it to kick off a run, or steer one that&apos;s already working.</span>
          </div>
        )}
        {stream.map(it => it.kind === 'step'
          ? <HistoryStep key={it.id} ev={it.ev} />
          : <ChatBubble key={it.id} bubble={{ id: it.id, author: it.kind === 'user' ? 'human' : 'agent', text: it.text, at: it.at }} />)}
        {working && <LiveEdge agentName={role?.displayName} context={liveContext} anchorAt={anchorAt} tokens={tokens} />}
        <div ref={endRef} />
      </div>
      <div className="border-t border-[var(--border)] p-2.5 relative">
        {slash && cmdItems.length > 0 && (
          <CommandMenu items={cmdItems} active={cmdActive} ctx={cmdCtx} onHover={setCmdActive} onPick={runCmd} />
        )}
        <textarea value={draft} onChange={e => setDraft(e.target.value)}
          onKeyDown={e => {
            if (slash && cmdItems.length) {
              if (e.key === 'ArrowDown') { e.preventDefault(); setCmdActive(a => Math.min(cmdItems.length - 1, a + 1)); return; }
              if (e.key === 'ArrowUp') { e.preventDefault(); setCmdActive(a => Math.max(0, a - 1)); return; }
              if (e.key === 'Enter') { e.preventDefault(); runCmd(cmdItems[cmdActive]); return; }
              if (e.key === 'Escape') { e.preventDefault(); setDraft(''); return; }
            }
            if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); send(); }
          }}
          rows={2} placeholder="Message the agent…  (/ for commands, ⌘/Ctrl + Enter to send)"
          className="w-full bg-[var(--surface)] rounded-lg p-2 text-[13px] outline-none resize-none text-[var(--foreground)] border border-[var(--border)] focus:border-[var(--primary)]" />
        <div className="flex justify-end mt-1.5">
          <Button variant="primary" size="sm" disabled={!draft.trim() || sending} onClick={send}><Icon.send size={13} /> Send</Button>
        </div>
      </div>
      {helpOpen && (
        <div className="absolute inset-0 z-30 bg-[var(--background)]/95 p-4 overflow-auto" onClick={() => setHelpOpen(false)}>
          <div className="text-sm font-semibold mb-2 text-[var(--foreground)]">Chat commands</div>
          {chatCommands.map(c => (
            <div key={c.name} className="py-1 text-[13px]">
              <span className="font-mono text-[var(--primary)]">/{c.name}</span>{' '}
              <span className="text-[var(--muted)]">{c.description}</span>
            </div>
          ))}
          <div className="text-[11px] text-[var(--muted-2)] mt-3">Click anywhere to close</div>
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 3: Replace `ChatBubble` and add `HistoryStep` + label helpers**

Replace the entire `ChatBubble` function with the following (drops the now-unused `tool` branch and adds the history-step renderer + helpers):

```tsx
function ChatBubble({ bubble }: { bubble: Bubble }) {
  const human = bubble.author === 'human';
  return (
    <div className={`flex ${human ? 'justify-end' : 'justify-start'}`}>
      <div className={`max-w-[80%] rounded-xl px-3 py-2 text-[13px] leading-snug whitespace-pre-wrap ${human ? 'bg-[var(--primary)] text-white rounded-br-sm' : 'bg-[var(--surface)] text-[var(--foreground)] rounded-bl-sm border border-[var(--border)]'}`}>
        {!human && <div className="flex items-center gap-1 text-[10px] text-[var(--muted)] mb-0.5"><Icon.robot size={11} /> Agent</div>}
        {bubble.text}
      </div>
    </div>
  );
}

// Friendly labels for the board-exploration tools the agent calls.
const TOOL_LABELS: Record<string, string> = {
  get_board_overview: 'Read board',
  search_tasks: 'Searched board',
  get_task: 'Read task',
  get_artifact: 'Read artifact',
  update_brief: 'Updated brief',
  request_human_input: 'Asked for input',
  request_approval: 'Requested approval',
  request_revision: 'Requested revision',
};

function stepLabel(ev: RunEvent): { verb: string; obj?: string; res?: string } {
  if (ev.kind === 'ArtifactWritten') return { verb: 'Saved deliverable' };
  const verb = TOOL_LABELS[ev.toolName ?? ''] ?? ev.toolName ?? String(ev.kind);
  return { verb, obj: ev.toolArgsSummary || undefined, res: ev.resultSummary || undefined };
}

// One real step in the history layer: round intent → subtle header; tool/artifact → ✓ line.
// A step still in flight (no result yet) shows a spinner — future per-tool streaming lights it up.
function HistoryStep({ ev }: { ev: RunEvent }) {
  if (ev.toolName === 'round_intent') {
    const intent = ev.toolArgsSummary || ev.title;
    if (!intent) return null;
    return (
      <div className="flex items-center gap-2 ml-1 mt-1 text-[11.5px] text-[var(--muted)]">
        <span className="text-[var(--primary)]">▸</span><span className="italic truncate">{intent}</span>
      </div>
    );
  }
  const running = !ev.resultSummary && ev.kind !== 'ArtifactWritten';
  const { verb, obj, res } = stepLabel(ev);
  return (
    <div className="flex items-center gap-2 ml-1 text-[12px] text-[var(--foreground)]">
      {running
        ? <span className="w-3.5 h-3.5 rounded-full border-2 border-[var(--border)] border-t-[var(--primary)] animate-spin shrink-0" />
        : <span className="w-4 h-4 rounded-full bg-[#00c875] text-white grid place-items-center text-[9px] shrink-0">✓</span>}
      <span className="flex-1 min-w-0 truncate">
        <span className="text-[var(--muted)]">{verb}</span>
        {obj && <span className="font-mono text-[var(--primary)]"> {obj}</span>}
        {res && <span className="text-[var(--muted-2)]"> · {res}</span>}
      </span>
    </div>
  );
}
```

- [ ] **Step 4: Typecheck**

Run:
```bash
cd src/web/tectika-board && npx tsc --noEmit
```
Expected: no errors. (If TS reports `Bubble` author type mismatch, confirm the `Bubble` type still declares `author: 'human' | 'agent' | 'tool'` — it does; we pass `'human' | 'agent'`, which is assignable.)

- [ ] **Step 5: Lint**

Run:
```bash
cd src/web/tectika-board && npx eslint src/components/workspace/ItemPanel.tsx
```
Expected: no errors/warnings.

- [ ] **Step 6: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): live chat history layer + working-state live edge"
```

---

## Task 5: Full build + smoke verification

**Files:** none (verification only)

- [ ] **Step 1: Production build**

Run:
```bash
cd src/web/tectika-board && npm run build -- --webpack
```
Expected: build succeeds with no type/lint/compile errors.

- [ ] **Step 2: Manual smoke checklist (deployed or local against the live API)**

Verify each, per the spec's testing section:

1. Open a task with an assigned agent, send a message → the **live edge** appears immediately (orb + rotating phrase + timer from `0:00`).
2. During the model-call gap (no new steps) the phrase keeps swapping (~3s) and the timer keeps ticking — it never reads as frozen.
3. As each round completes, real steps commit into history (`✓ Read board`, `✓ Searched board …`, round-intent header `▸ …`), and tokens update.
4. **Navigate away and back** while the run is still going (`task.status === 'InProgress'`) → the live edge is still present, timer showing true elapsed. (This is the bug being fixed.)
5. When the agent produces its final answer, the live edge disappears and the answer bubble remains; history is fully concrete.
6. `/clear` still hides prior history (events before `chatClearedAt`) and the live edge/token count reflect only the post-clear turn.

- [ ] **Step 3: Final commit (if the checklist required any fix)**

If steps above needed changes, commit them:
```bash
git add -A && git commit -m "fix(web): live chat streaming smoke fixes"
```
Otherwise nothing to commit; the feature is complete on the branch.

---

## Notes for the implementer

- **Why `task.status` and not the run document:** `task.status === 'InProgress'` is the authoritative, live-synced "working" flag the backend maps every run state onto (`board-context.tsx:659`). It is already kept fresh by board-context's SSE + polling, so it survives the chat panel unmounting/remounting — which is exactly why it fixes the navigate-away bug.
- **Why the live edge can't look stuck:** even with zero events for 30s, the phrase swaps every 3s and the timer ticks every 1s — new information every second.
- **Honesty:** checkmarks, tool names, and token counts come only from real `RunEvent`s. The rotating phrase is the only synthetic element and is clearly flavor (and `aria-hidden`).
- **No backend changes** — do not touch `src/workflows`, `src/api`, `src/agentruntime`, or `src/core`. The events and the working signal already exist.
```
