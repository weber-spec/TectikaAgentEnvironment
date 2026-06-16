'use client';

import React, { useEffect, useState, useRef, useMemo } from 'react';
import { createPortal } from 'react-dom';
import { useBoard } from '@/lib/board-context';
import { api } from '@/lib/api';
import type { Artifact, AgentTask, AgentRole, RunEvent, HumanInteraction } from '@/lib/types';
import { STATUS_CONFIG, STATUS_ORDER, PRIORITY_CONFIG, PRIORITY_ORDER, textOn } from '@/lib/palette';
import { Avatar, Pill, Button, Spinner } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { Popover } from '@/components/ui/overlays';
import { formatDateTime, relativeTime, displayName } from '@/lib/format';
import { toast } from '@/lib/toast';
import { CliInstallGuide } from './CliInstallGuide';
import { CommandMenu } from './CommandMenu';
import { chatCommands, filterCommands, type ChatCommand, type ChatCommandContext } from '@/lib/chat-commands';
import { RunTaskButton } from '@/components/board/RunTaskButton';
import { LiveEdge } from './LiveEdge';
import { contextFromEvents, sumTokens } from '@/lib/thinking-phrases';
import { InteractionCard } from '@/components/InteractionCard';

type Tab = 'chat' | 'activity' | 'details' | 'bridge';

// Models offered in the in-panel agent configuration dropdown.
const MODELS = ['default', 'gpt-4o', 'o3', 'claude-opus-4-8', 'claude-sonnet-4-6', 'claude-haiku-4-5'];
const TOOL_LIBRARY = ['search', 'read_repo', 'write_code', 'run_tests', 'deploy', 'browser', 'sql', 'http'];

export function ItemPanel() {
  const { openTaskId, openTask, tasks } = useBoard();
  const task = tasks.find(t => t.id === openTaskId);
  if (!openTaskId || !task) return null;
  return createPortal(
    <div className="fixed inset-0 z-[1200] flex justify-end" style={{ background: 'rgba(0,0,0,0.35)' }} onMouseDown={() => openTask(undefined)}>
      <div className="bg-[var(--background)] h-full w-full max-w-[940px] shadow-2xl flex flex-col animate-slide-in-right" onMouseDown={e => e.stopPropagation()}>
        <PanelInner task={task} />
      </div>
    </div>,
    document.body,
  );
}

function PanelInner({ task }: { task: AgentTask }) {
  const { openTask, roles, runsById, updateTask, setStatus, peopleById, people } = useBoard();
  const [tab, setTab] = useState<Tab>('chat');

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') openTask(undefined); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [openTask]);

  const role = roles.find(r => r.id === task.assignee.id);
  const run = task.workflowRunId ? runsById[task.workflowRunId] : undefined;
  const person = peopleById[task.assignee.id];

  return (
    <>
      {/* header */}
      <div className="px-5 py-3 border-b border-[var(--border)] flex items-start gap-3">
        <div className="flex-1 min-w-0">
          <TitleEdit task={task} onSave={t => updateTask(task.id, { title: t })} />
          <div className="flex items-center gap-2 mt-2 flex-wrap">
            <HeaderStatus task={task} onPick={s => setStatus(task.id, s)} />
            <HeaderPriority task={task} onPick={p => updateTask(task.id, { priority: p })} />
            <div className="flex items-center gap-1.5 text-xs text-[var(--muted)]">
              <Avatar person={person} name={task.assignee.id} size={22} />
              {person?.name ?? displayName(task.assignee.id)}
            </div>
            {task.dueAt && <span className="text-xs text-[var(--muted)] inline-flex items-center gap-1"><Icon.calendar size={13} /> {formatDateTime(task.dueAt)}</span>}
          </div>
        </div>
        <RunTaskButton task={task} />
        <button onClick={() => openTask(undefined)} className="w-8 h-8 flex items-center justify-center rounded-md text-[var(--muted)] hover:bg-[var(--surface)] shrink-0"><Icon.x size={18} /></button>
      </div>

      {/* dual-pane body */}
      <div className="flex-1 flex min-h-0">
        {/* left: execution thread / config */}
        <div className="w-[420px] shrink-0 border-r border-[var(--border)] flex flex-col min-h-0">
          <div className="flex border-b border-[var(--border)] px-2 overflow-x-auto whitespace-nowrap">
            {(['chat', 'activity', 'details', 'bridge'] as Tab[]).map(t => (
              <button key={t} onClick={() => setTab(t)} className={`px-3 py-2.5 text-[13px] font-medium capitalize border-b-2 -mb-px transition-colors shrink-0 ${tab === t ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>{t === 'chat' ? 'Chat' : t === 'bridge' ? 'CLI Bridge' : t}</button>
            ))}
          </div>
          <div className="flex-1 overflow-auto flex flex-col min-h-0">
            {tab === 'chat' && <ChatTab task={task} role={role} />}
            {tab === 'activity' && <ActivityTab task={task} />}
            {tab === 'details' && <DetailsTab task={task} role={role} run={run} people={people} onAssign={(id, kind) => updateTask(task.id, { assignee: { type: kind, id } })} />}
            {tab === 'bridge' && <CliBridgeTab task={task} />}
          </div>
        </div>
        {/* right: evolving artifact */}
        <div className="flex-1 min-w-0 flex flex-col bg-[var(--surface)]/30">
          <ArtifactPane task={task} />
        </div>
      </div>
    </>
  );
}

function TitleEdit({ task, onSave }: { task: AgentTask; onSave: (t: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [v, setV] = useState(task.title);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- sync editable field when the open task changes
  useEffect(() => setV(task.title), [task.title]);
  if (editing) return (
    <input autoFocus value={v} onChange={e => setV(e.target.value)} onBlur={() => { setEditing(false); if (v.trim()) onSave(v.trim()); }}
      onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
      className="text-lg font-semibold w-full bg-transparent outline-none border-b border-[var(--primary)] text-[var(--foreground)]" />
  );
  return <h2 className="text-lg font-semibold text-[var(--foreground)] cursor-text truncate" onClick={() => setEditing(true)}>{task.title}</h2>;
}

function HeaderStatus({ task, onPick }: { task: AgentTask; onPick: (s: AgentTask['status']) => void }) {
  const ref = useRef<HTMLDivElement>(null); const [o, setO] = useState(false);
  return <div ref={ref} className="inline-block"><Pill label={STATUS_CONFIG[task.status].label} hex={STATUS_CONFIG[task.status].hex} dropdown onClick={() => setO(v => !v)} />
    <Popover anchorRef={ref} open={o} onClose={() => setO(false)} width={180} className="p-2 flex flex-col gap-1">
      {STATUS_ORDER.map(s => <button key={s} onClick={() => { onPick(s); setO(false); }} className="rounded px-2 py-1.5 text-[13px] font-semibold text-left" style={{ background: STATUS_CONFIG[s].hex, color: textOn(STATUS_CONFIG[s].hex) }}>{STATUS_CONFIG[s].label}</button>)}
    </Popover></div>;
}
function HeaderPriority({ task, onPick }: { task: AgentTask; onPick: (p: AgentTask['priority']) => void }) {
  const ref = useRef<HTMLDivElement>(null); const [o, setO] = useState(false);
  return <div ref={ref} className="inline-block"><Pill label={PRIORITY_CONFIG[task.priority].label} hex={PRIORITY_CONFIG[task.priority].hex} dropdown onClick={() => setO(v => !v)} />
    <Popover anchorRef={ref} open={o} onClose={() => setO(false)} width={160} className="p-2 flex flex-col gap-1">
      {PRIORITY_ORDER.map(s => <button key={s} onClick={() => { onPick(s); setO(false); }} className="rounded px-2 py-1.5 text-[13px] font-semibold text-left" style={{ background: PRIORITY_CONFIG[s].hex, color: textOn(PRIORITY_CONFIG[s].hex) }}>{PRIORITY_CONFIG[s].label}</button>)}
    </Popover></div>;
}

// ── Live + replayable run trace (shared by Chat and Activity) ─────────────────
// Loads the persisted RunEvents for a task, then appends live `run_event`s over SSE.
function useRunEvents(task: AgentTask, activeRunId?: string): RunEvent[] {
  const [events, setEvents] = useState<RunEvent[]>([]);

  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- clear stale trace when switching tasks
    setEvents([]);
    api.tasks.events(task.boardId, task.id).then(list => { if (alive) setEvents(list); }).catch(() => {});
    return () => { alive = false; };
  }, [task.boardId, task.id]);

  useEffect(() => {
    const runId = activeRunId ?? task.workflowRunId;
    if (!runId) return;
    const stop = api.streamRun(runId, (e) => {
      if (e.type !== 'run_event') return;
      const re: RunEvent = {
        id: e.eventId ?? `${e.runId}-${e.timestamp}`,
        taskId: e.taskId ?? task.id, runId: e.runId, round: e.round ?? 0,
        parentId: e.parentId, kind: e.kind ?? 'Thinking', title: e.title, detail: e.content,
        toolName: e.toolName, toolArgsSummary: e.toolArgsSummary, resultSummary: e.resultSummary,
        tokenUsage: e.tokenUsage, timestamp: e.timestamp,
      };
      setEvents(prev => prev.some(x => x.id === re.id) ? prev : [...prev, re]);
    });
    return stop;
  }, [activeRunId, task.workflowRunId, task.id]);

  // Hide events before the /clear boundary (non-destructive — they stay in the DB).
  return useMemo(
    () => (task.chatClearedAt ? events.filter(e => e.timestamp > task.chatClearedAt!) : events),
    [events, task.chatClearedAt],
  );
}

// ── Activity — hierarchical round → sub-activity timeline ─────────────────────
function ActivityTab({ task }: { task: AgentTask }) {
  const events = useRunEvents(task);
  const rounds = useMemo(() => {
    const tops = events.filter(e => !e.parentId).sort((a, b) => +new Date(a.timestamp) - +new Date(b.timestamp));
    return tops.map(top => ({ top, children: events.filter(e => e.parentId === top.id).sort((a, b) => +new Date(a.timestamp) - +new Date(b.timestamp)) }));
  }, [events]);

  if (rounds.length === 0)
    return <div className="p-6 text-center text-sm text-[var(--muted)]"><Icon.bolt size={30} className="mx-auto mb-2 text-[var(--muted-2)]" />No agent activity yet.<br /><span className="text-xs">Start the agent (or chat) to see its steps here.</span></div>;

  return (
    <div className="p-3 flex flex-col gap-1.5">
      {rounds.map(r => <ActivityRow key={r.top.id} ev={r.top} subs={r.children} />)}
    </div>
  );
}

function ActivityRow({ ev, subs }: { ev: RunEvent; subs: RunEvent[] }) {
  const [open, setOpen] = useState(false);
  const hasChildren = subs.length > 0;
  const label = ev.kind === 'RoundStarted' ? (ev.title || 'Working…')
    : ev.kind === 'UserMessage' ? `You: ${ev.title}`
    : ev.kind === 'AgentMessage' ? (ev.title || 'Agent replied')
    : (ev.title || ev.kind);
  return (
    <div className="rounded-lg border border-[var(--border)] bg-[var(--surface)]/40">
      <button disabled={!hasChildren} onClick={() => setOpen(o => !o)}
        className="w-full flex items-center gap-2 px-2.5 py-2 text-left text-[13px]">
        <Icon.bolt size={13} className="text-[#fdab3d] shrink-0" />
        <span className="flex-1 truncate text-[var(--foreground)]">{label}</span>
        <span className="text-[10px] text-[var(--muted-2)]">{relativeTime(ev.timestamp)}</span>
        {hasChildren && <Icon.chevronDown size={14} className={`text-[var(--muted)] transition-transform ${open ? 'rotate-180' : ''}`} />}
      </button>
      {open && hasChildren && (
        <div className="px-2.5 pb-2 flex flex-col gap-1 border-t border-[var(--border)] pt-1.5">
          {subs.map(c => (
            <div key={c.id} className="flex items-start gap-2 text-[12px] text-[var(--muted)] pl-1">
              <Icon.bolt size={11} className="mt-0.5 text-[var(--muted-2)] shrink-0" />
              <span className="flex-1">
                <span className="font-mono text-[var(--foreground)]">{c.toolName ?? c.kind}</span>
                {c.toolArgsSummary ? <span className="text-[var(--muted-2)]"> {c.toolArgsSummary}</span> : null}
                {c.resultSummary ? <span className="text-[var(--muted)]"> → {c.resultSummary}</span> : null}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── QA Feedback Loop section ──────────────────────────────────────────────────
function QaLoopSection({ task }: { task: AgentTask }) {
  const { edges, updateEdge } = useBoard();
  const feedbackEdges = edges.filter(
    e => e.sourceTaskId === task.id && e.kind === 'QaFeedback'
  );
  if (feedbackEdges.length === 0) return null;

  return (
    <div className="rounded-lg border border-[var(--border)] p-3 flex flex-col gap-2">
      <div className="flex items-center gap-2">
        <span style={{ color: '#ff642e' }}>↻</span>
        <span className="font-semibold text-sm">QA Feedback Loop</span>
      </div>
      {feedbackEdges.map(edge => (
        <div key={edge.id} className="flex items-center gap-2">
          <label className="text-xs text-[var(--muted)]">Max iterations</label>
          <input
            type="number" min={1} max={20}
            defaultValue={edge.maxIterations}
            onBlur={ev => {
              const val = parseInt(ev.target.value, 10);
              if (!isNaN(val) && val > 0 && val !== edge.maxIterations)
                updateEdge(edge.id, { maxIterations: val });
            }}
            className="w-16 bg-[var(--surface)] border border-[var(--border)] rounded px-2 py-1 text-sm"
          />
          <span className="text-xs text-[var(--muted)]">
            ({edge.currentIterations ?? 0} used)
          </span>
        </div>
      ))}
    </div>
  );
}

// ── Details / config ──────────────────────────────────────────────────────────
function DetailsTab({ task, role, run, people, onAssign }: { task: AgentTask; role?: ReturnType<typeof useBoard>['roles'][0]; run?: ReturnType<typeof useBoard>['runsById'][string]; people: ReturnType<typeof useBoard>['people']; onAssign: (id: string, kind: 'Agent' | 'Human') => void }) {
  const { updateTask } = useBoard();
  return (
    <div className="p-4 flex flex-col gap-4 text-sm">
      <Field label="Description">
        <textarea defaultValue={task.description} onBlur={e => { if (e.target.value !== task.description) updateTask(task.id, { description: e.target.value }); }}
          placeholder="Add a description…" rows={3} className="w-full bg-[var(--surface)] rounded-lg p-2 outline-none resize-none text-[var(--foreground)] border border-[var(--border)] focus:border-[var(--primary)]" />
      </Field>
      <Field label="Task prompt">
        <textarea defaultValue={task.prompt ?? ''} onBlur={e => { if (e.target.value !== (task.prompt ?? '')) updateTask(task.id, { prompt: e.target.value }); }}
          placeholder="Specific instructions for THIS task — layered on top of the agent's role/persona…" rows={4}
          className="w-full bg-[var(--surface)] rounded-lg p-2 outline-none resize-none text-[var(--foreground)] border border-[var(--border)] focus:border-[var(--primary)]" />
        <div className="text-[11px] text-[var(--muted-2)] mt-1">The agent keeps its role&apos;s skills/persona; this tells it exactly what to do here.</div>
      </Field>
      <Field label="Owner">
        <select value={task.assignee.id} onChange={e => { const p = people.find(x => x.id === e.target.value); onAssign(e.target.value, p?.kind ?? 'Human'); }}
          className="w-full bg-[var(--surface)] rounded-lg p-2 outline-none text-[var(--foreground)] border border-[var(--border)]">
          <optgroup label="Agents">{people.filter(p => p.kind === 'Agent').map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</optgroup>
          <optgroup label="People">{people.filter(p => p.kind === 'Human').map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</optgroup>
        </select>
      </Field>
      {role && <AgentConfigEditor role={role} />}
      {run && (
        <div className="rounded-lg border border-[var(--border)] p-3 bg-[var(--background)]">
          <div className="flex items-center gap-2 mb-2"><Icon.bolt size={16} className="text-[#fdab3d]" /><span className="font-semibold text-[var(--foreground)]">Latest run</span></div>
          <div className="grid grid-cols-3 gap-2 text-center">
            <Stat label="Status" value={run.status} />
            <Stat label="Tokens" value={run.totalTokens.toLocaleString()} />
            <Stat label="Cost" value={`$${run.estimatedCostUsd.toFixed(2)}`} />
          </div>
        </div>
      )}
      <QaLoopSection task={task} />
    </div>
  );
}
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <div><div className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold mb-1">{label}</div>{children}</div>;
}
function Stat({ label, value }: { label: string; value: string }) {
  return <div className="bg-[var(--surface)] rounded-lg py-2"><div className="text-[10px] uppercase text-[var(--muted)]">{label}</div><div className="text-sm font-semibold text-[var(--foreground)]">{value}</div></div>;
}

// ── Editable agent configuration (model / prompt / tools) ─────────────────────
function AgentConfigEditor({ role }: { role: AgentRole }) {
  const { saveRole } = useBoard();
  const [prompt, setPrompt] = useState(role.systemPrompt);
  const [toolMenu, setToolMenu] = useState(false);
  const toolRef = useRef<HTMLButtonElement>(null);
  // keep the editable prompt in sync when a different agent is opened
  const lastId = useRef(role.id);
  if (lastId.current !== role.id) { lastId.current = role.id; if (prompt !== role.systemPrompt) setPrompt(role.systemPrompt); }

  const setModel = (m: string) => saveRole({ ...role, modelOverride: m === 'default' ? undefined : m });
  const toggleTool = (t: string) => saveRole({ ...role, tools: role.tools.includes(t) ? role.tools.filter(x => x !== t) : [...role.tools, t] });
  const available = TOOL_LIBRARY.filter(t => !role.tools.includes(t));

  return (
    <div className="rounded-lg border border-[var(--border)] p-3 bg-[var(--background)] flex flex-col gap-3">
      <div className="flex items-center gap-2"><Icon.robot size={16} className="text-[var(--primary)]" /><span className="font-semibold text-[var(--foreground)]">Agent configuration</span><span className="text-[10px] text-[var(--muted)] ml-auto">{role.displayName}</span></div>

      <Field label="Model">
        <div className="relative">
          <select value={role.modelOverride ?? 'default'} onChange={e => setModel(e.target.value)}
            className="w-full appearance-none bg-[var(--surface)] rounded-lg pl-2.5 pr-8 py-2 text-[13px] text-[var(--foreground)] border border-[var(--border)] outline-none focus:border-[var(--primary)] cursor-pointer">
            {MODELS.map(m => <option key={m} value={m}>{m === 'default' ? 'Default (workspace model)' : m}</option>)}
          </select>
          <Icon.chevronDown size={14} className="absolute right-2.5 top-1/2 -translate-y-1/2 text-[var(--muted)] pointer-events-none" />
        </div>
      </Field>

      <Field label="System prompt">
        <textarea value={prompt} onChange={e => setPrompt(e.target.value)}
          onBlur={() => { if (prompt !== role.systemPrompt) saveRole({ ...role, systemPrompt: prompt }); }}
          rows={4} placeholder="Describe how this agent should behave…"
          className="w-full bg-[var(--surface)] rounded-lg p-2 text-[12.5px] outline-none resize-none text-[var(--foreground)] border border-[var(--border)] focus:border-[var(--primary)] leading-snug" />
      </Field>

      <Field label="Tools">
        <div className="flex flex-wrap items-center gap-1.5">
          {role.tools.length === 0 && <span className="text-xs text-[var(--muted-2)]">No tools assigned</span>}
          {role.tools.map(t => (
            <span key={t} className="group/tool inline-flex items-center gap-1 pl-2 pr-1 py-0.5 rounded-md text-[11px] font-medium bg-[#0086c022] text-[#0086c0]">
              {t}
              <button onClick={() => toggleTool(t)} className="opacity-50 hover:opacity-100 hover:text-[#e2445c]" title="Remove tool"><Icon.x size={10} /></button>
            </span>
          ))}
          <button ref={toolRef} onClick={() => setToolMenu(o => !o)} disabled={available.length === 0}
            className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-md text-[11px] font-medium border border-dashed border-[var(--border)] text-[var(--muted)] hover:text-[var(--primary)] hover:border-[var(--primary)] disabled:opacity-40">
            <Icon.plus size={11} /> tool
          </button>
          <Popover anchorRef={toolRef} open={toolMenu} onClose={() => setToolMenu(false)} width={170} className="p-1">
            <div className="flex flex-col gap-0.5">
              {available.map(t => <button key={t} onClick={() => { toggleTool(t); setToolMenu(false); }} className="w-full text-left px-2 py-1.5 rounded hover:bg-[var(--surface)] text-[13px] text-[var(--foreground)]">{t}</button>)}
            </div>
          </Popover>
        </div>
      </Field>
      <p className="text-[10px] text-[var(--muted-2)]">Edits update this reusable agent role across the workspace.</p>
    </div>
  );
}

// ── Interactive agent conversation (steerable: start-or-inject) ───────────────
type Bubble = { id: string; author: 'human' | 'agent' | 'tool'; text: string; toolName?: string; at: string };

function ChatTab({ task, role }: { task: AgentTask; role?: AgentRole }) {
  // No agent owns this task → can't chat. Prompt to assign one first.
  if (task.assignee.type !== 'Agent') return <AssignAgentPrompt task={task} />;
  return <AgentChat task={task} role={role} />;
}

function AssignAgentPrompt({ task }: { task: AgentTask }) {
  const { people, updateTask } = useBoard();
  const agents = people.filter(p => p.kind === 'Agent');
  return (
    <div className="flex flex-col items-center justify-center h-full p-6 text-center">
      <Icon.robot size={40} className="text-[var(--muted-2)] mb-3" />
      <h3 className="text-sm font-semibold text-[var(--foreground)]">No agent assigned</h3>
      <p className="text-xs text-[var(--muted)] mt-1 mb-4 max-w-[260px]">
        Assign an agent to this task to start a conversation and run it.
      </p>
      <select value="" onChange={e => { if (e.target.value) updateTask(task.id, { assignee: { type: 'Agent', id: e.target.value } }); }}
        className="w-full max-w-[260px] bg-[var(--surface)] rounded-lg p-2 text-[13px] outline-none text-[var(--foreground)] border border-[var(--border)] focus:border-[var(--primary)] cursor-pointer">
        <option value="" disabled>Choose an agent…</option>
        {agents.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select>
    </div>
  );
}

function AgentChat({ task, role }: { task: AgentTask; role?: AgentRole }) {
  const [activeRunId, setActiveRunId] = useState<string | undefined>(task.workflowRunId);
  const events = useRunEvents(task, activeRunId);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [pending, setPending] = useState<Bubble[]>([]);   // optimistic human turns (echo isn't streamed live)
  const [justSent, setJustSent] = useState(false);        // bridge until task.status syncs to InProgress
  const [pendingInteraction, setPendingInteraction] = useState<HumanInteraction | null>(null);

  // When the run is paused on an agent request, load the pending interaction for this task so we can
  // render its card inline. Cleared as soon as the task is no longer awaiting.
  useEffect(() => {
    if (task.status !== 'AwaitingInteraction') {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- clear card when not awaiting
      setPendingInteraction(null);
      return;
    }
    let alive = true;
    api.interactions.pending()
      .then(list => { if (alive) setPendingInteraction(list.find(i => i.taskId === task.id) ?? null); })
      .catch(() => {});
    return () => { alive = false; };
  }, [task.status, task.id]);
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
        {pendingInteraction && (
          <InteractionCard
            interaction={pendingInteraction}
            onResponded={() => { setPendingInteraction(null); refreshTask(task.id); }}
          />
        )}
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
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
          }}
          rows={2} placeholder="Message the agent…  (/ for commands, Enter to send, Shift+Enter for newline)"
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

// ── External CLI bridge (stream a local agent into this task) ─────────────────
type TermLine = { id: number; kind: 'sys' | 'stdout' | 'meta' | 'add' | 'del'; text: string };

function classifyLine(l: string): TermLine['kind'] {
  if (/^(diff --git|index |@@|\+\+\+|---)/.test(l)) return 'meta';
  if (l.startsWith('+')) return 'add';
  if (l.startsWith('-')) return 'del';
  return 'stdout';
}
function TerminalLine({ line }: { line: TermLine }) {
  const color = ({ sys: '#7aa2f7', stdout: 'rgba(255,255,255,0.85)', meta: '#9aa0b4', add: '#42d392', del: '#ff6b6b' } as const)[line.kind];
  return <div style={{ color, whiteSpace: 'pre-wrap' }}>{line.text}</div>;
}

function CliBridgeTab({ task }: { task: AgentTask }) {
  const runId = task.workflowRunId ?? `run-${task.id}`;
  const [connected, setConnected] = useState(false);
  const [lines, setLines] = useState<TermLine[]>([]);
  const [demo, setDemo] = useState(false);
  const [guideOpen, setGuideOpen] = useState(false);
  const seq = useRef(0);
  const endRef = useRef<HTMLDivElement>(null);
  const timers = useRef<number[]>([]);
  const cmd = `agentboard link --task-id ${task.id} --run-id ${runId}`;

  const push = (kind: TermLine['kind'], text: string) => setLines(prev => [...prev, { id: seq.current++, kind, text }]);

  useEffect(() => () => { timers.current.forEach(clearTimeout); }, []);

  // poll CLI connection status
  useEffect(() => {
    let active = true;
    const poll = () => api.cliStatus(task.id).then(s => { if (active) setConnected(s.connected); }).catch(() => {});
    poll();
    const t = window.setInterval(poll, 4000);
    return () => { active = false; clearInterval(t); };
  }, [task.id]);

  // receive cli_* events over the run's SSE stream
  useEffect(() => {
    if (!task.workflowRunId) return;
    return api.streamRun(task.workflowRunId, (ev) => {
      if (ev.taskId && ev.taskId !== task.id) return;
      if (ev.type === 'cli_connected') { setConnected(true); push('sys', '✓ local agent connected'); }
      else if (ev.type === 'cli_disconnected') { setConnected(false); push('sys', '× local agent disconnected'); }
      else if (ev.type === 'cli_output' && ev.content) ev.content.split('\n').forEach(l => push(classifyLine(l), l));
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [task.workflowRunId, task.id]);

  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [lines.length]);

  const copy = () => navigator.clipboard?.writeText(cmd).then(() => toast('Command copied', 'success')).catch(() => {});

  const playDemo = () => {
    if (demo) return;
    setDemo(true); setLines([]); seq.current = 0;
    const steps: Array<[number, () => void]> = [
      [200, () => push('sys', `$ ${cmd}`)],
      [700, () => { push('sys', '✓ connected — streaming local session'); setConnected(true); }],
      [1100, () => push('stdout', '> npm run build')],
      [1500, () => push('stdout', '✓ compiled in 1.24s')],
      [1900, () => push('stdout', '> npm test  (42 specs)')],
      [2500, () => push('stdout', '✓ 42 passing')],
      [2900, () => push('meta', 'diff --git a/src/checkout.ts b/src/checkout.ts')],
      [3100, () => push('del', '- export function total(items) {')],
      [3250, () => push('add', '+ export function total(items: Item[]): number {')],
      [3400, () => push('add', '+   if (!items.length) return 0;')],
      [3750, () => push('stdout', '> git commit -m "harden checkout total"')],
      [4200, () => { push('sys', '✓ artifact synced to the canvas'); setConnected(false); setDemo(false); }],
    ];
    timers.current = steps.map(([d, fn]) => window.setTimeout(fn, d));
  };

  return (
    <div className="flex flex-col h-full min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-[var(--border)]">
        <Icon.flow size={14} className="text-[var(--primary)]" />
        <span className="text-[13px] font-semibold text-[var(--foreground)]">CLI Bridge</span>
        <span className="ml-auto inline-flex items-center gap-1.5 text-[11px] font-medium px-2 py-0.5 rounded-full"
          style={connected ? { background: '#00c87522', color: '#00897b' } : { background: 'var(--surface)', color: 'var(--muted)' }}>
          <span className="w-1.5 h-1.5 rounded-full" style={{ background: connected ? '#00c875' : '#c4c4c4' }} />
          {connected ? 'Connected' : 'Listening'}
        </span>
      </div>

      <div className="p-3 flex flex-col gap-3 min-h-0 flex-1">
        <div className="flex items-start justify-between gap-2">
          <div className="text-xs text-[var(--muted)]">Stream a local agent (Claude Code, Cursor, a terminal loop) into this task — its stdout, git diffs and artifacts appear here and on the board.</div>
          <button onClick={() => setGuideOpen(true)} className="shrink-0 inline-flex items-center gap-1 text-[11px] font-medium text-[var(--primary)] hover:underline whitespace-nowrap"><Icon.file size={12} /> How to install</button>
        </div>
        <div className="flex items-stretch gap-1.5">
          <code className="flex-1 bg-[var(--surface)] border border-[var(--border)] rounded-lg px-2.5 py-2 text-[11.5px] font-mono text-[var(--foreground)] overflow-x-auto whitespace-nowrap">{cmd}</code>
          <button onClick={copy} title="Copy command" className="shrink-0 px-2 rounded-lg border border-[var(--border)] text-[var(--muted)] hover:text-[var(--primary)] hover:border-[var(--primary)]"><Icon.duplicate size={14} /></button>
        </div>
        <CliInstallGuide open={guideOpen} onClose={() => setGuideOpen(false)} taskId={task.id} runId={runId} />

        <div className="flex-1 min-h-[180px] rounded-lg overflow-hidden border border-[var(--border)] flex flex-col" style={{ background: '#0f1117' }}>
          <div className="flex items-center gap-1.5 px-2.5 py-1.5 border-b border-white/10">
            <span className="w-2 h-2 rounded-full bg-[#ff5f57]" /><span className="w-2 h-2 rounded-full bg-[#febc2e]" /><span className="w-2 h-2 rounded-full bg-[#28c840]" />
            <span className="text-[10px] text-white/40 ml-1 font-mono truncate">local agent · {task.id}</span>
          </div>
          <div className="flex-1 overflow-auto p-2.5 font-mono text-[11.5px] leading-relaxed">
            {lines.length === 0
              ? <div className="text-white/40">Waiting for a local agent to connect…</div>
              : lines.map(l => <TerminalLine key={l.id} line={l} />)}
            <div ref={endRef} />
          </div>
        </div>

        <div className="flex items-center justify-between gap-2">
          <span className="text-[10px] text-[var(--muted-2)]">Live tunnel via WebSocket → board &amp; canvas</span>
          <Button size="sm" disabled={demo} onClick={playDemo}><Icon.bolt size={13} /> {demo ? 'Streaming…' : 'Play demo session'}</Button>
        </div>
      </div>
    </div>
  );
}

// ── Evolving artifact ───────────────────────────────────────────────────────────
function ArtifactPane({ task }: { task: AgentTask }) {
  const [versions, setVersions] = useState<Artifact[] | null>(null);
  const [idx, setIdx] = useState(0);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [saving, setSaving] = useState(false);

  const load = () => { api.artifacts.versions(task.id).then(v => { setVersions(v); setIdx(0); }).catch(() => setVersions([])); };
  useEffect(load, [task.id]);

  const current = versions?.[idx];

  const save = async () => {
    if (!current) return;
    setSaving(true);
    try { await api.artifacts.save(task.id, draft, current.contentType, current.runId); setEditing(false); load(); }
    finally { setSaving(false); }
  };

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center justify-between px-4 py-2.5 border-b border-[var(--border)]">
        <div className="flex items-center gap-2"><Icon.file size={16} className="text-[var(--muted)]" /><span className="text-sm font-semibold text-[var(--foreground)]">Evolving Artifact</span></div>
        {versions && versions.length > 0 && (
          <div className="flex items-center gap-2">
            <select value={idx} onChange={e => setIdx(Number(e.target.value))} className="text-xs bg-[var(--surface)] rounded px-2 py-1 outline-none border border-[var(--border)] text-[var(--foreground)]">
              {versions.map((v, i) => <option key={v.id} value={i}>v{v.version} · {v.origin}</option>)}
            </select>
            {!editing && current && <button onClick={() => { setDraft(current.content); setEditing(true); }} className="text-xs text-[var(--primary)] inline-flex items-center gap-1 hover:underline"><Icon.edit size={13} /> Edit</button>}
          </div>
        )}
      </div>
      <div className="flex-1 overflow-auto p-4">
        {versions === null ? (
          <div className="flex items-center justify-center h-full gap-2 text-[var(--muted)]"><Spinner /> Loading artifact…</div>
        ) : versions.length === 0 ? (
          <div className="text-center text-[var(--muted)] mt-10 text-sm">
            <Icon.file size={40} className="mx-auto mb-3 text-[var(--muted-2)]" />
            No artifact produced yet.<br />The agent&apos;s deliverable will appear here as it works.
          </div>
        ) : editing ? (
          <textarea autoFocus value={draft} onChange={e => setDraft(e.target.value)} className="w-full h-full font-mono text-[12.5px] bg-[var(--background)] border border-[var(--primary)] rounded-lg p-3 outline-none resize-none text-[var(--foreground)]" />
        ) : current ? (
          <ArtifactBody artifact={current} />
        ) : null}
      </div>
      {editing && (
        <div className="border-t border-[var(--border)] p-3 flex justify-end gap-2">
          <Button size="sm" onClick={() => setEditing(false)}>Cancel</Button>
          <Button variant="primary" size="sm" disabled={saving} onClick={save}>{saving ? 'Saving…' : 'Save new version'}</Button>
        </div>
      )}
    </div>
  );
}

function ArtifactBody({ artifact }: { artifact: Artifact }) {
  return (
    <div>
      {artifact.inputContext.upstreamArtifacts.length > 0 && (
        <div className="mb-3 text-[11px] text-[var(--muted)]">
          <span className="font-semibold">Input context:</span> {artifact.inputContext.upstreamArtifacts.map(u => `${u.contentType} from ${u.taskId} v${u.version}`).join(', ')}
          {artifact.inputContext.humanContext && <div className="italic mt-1">“{artifact.inputContext.humanContext}”</div>}
        </div>
      )}
      {artifact.contentType === 'Markdown'
        ? <Markdown text={artifact.content} />
        : <pre className="font-mono text-[12.5px] bg-[var(--background)] border border-[var(--border)] rounded-lg p-3 overflow-auto whitespace-pre-wrap text-[var(--foreground)]">{artifact.content}</pre>}
      {artifact.internalLogs.length > 0 && (
        <div className="mt-3 text-[11px]">
          <div className="uppercase tracking-wide text-[var(--muted)] font-semibold mb-1">Execution log</div>
          {artifact.internalLogs.map((l, i) => <div key={i} className="text-[var(--muted)] font-mono">› {l}</div>)}
        </div>
      )}
    </div>
  );
}

// minimal markdown: headings, lists, code fences, bold
function Markdown({ text }: { text: string }) {
  const lines = text.split('\n');
  const out: React.ReactNode[] = [];
  let code: string[] | null = null;
  lines.forEach((ln, i) => {
    if (ln.startsWith('```')) { if (code) { out.push(<pre key={i} className="font-mono text-[12px] bg-[var(--background)] border border-[var(--border)] rounded p-2 my-2 overflow-auto">{code.join('\n')}</pre>); code = null; } else code = []; return; }
    if (code) { code.push(ln); return; }
    if (ln.startsWith('### ')) out.push(<h4 key={i} className="font-semibold text-[var(--foreground)] mt-2">{ln.slice(4)}</h4>);
    else if (ln.startsWith('## ')) out.push(<h3 key={i} className="font-bold text-[var(--foreground)] text-base mt-3">{ln.slice(3)}</h3>);
    else if (ln.startsWith('# ')) out.push(<h2 key={i} className="font-bold text-[var(--foreground)] text-lg mt-3">{ln.slice(2)}</h2>);
    else if (ln.startsWith('- ')) out.push(<li key={i} className="text-[13px] text-[var(--foreground)] ml-4 list-disc">{inlineBold(ln.slice(2))}</li>);
    else if (ln.trim() === '') out.push(<div key={i} className="h-2" />);
    else out.push(<p key={i} className="text-[13px] text-[var(--foreground)]">{inlineBold(ln)}</p>);
  });
  if (code) out.push(<pre key="last" className="font-mono text-[12px] bg-[var(--background)] border border-[var(--border)] rounded p-2 my-2 overflow-auto">{(code as string[]).join('\n')}</pre>);
  return <div>{out}</div>;
}
function inlineBold(s: string) {
  return s.split(/(\*\*[^*]+\*\*|`[^`]+`)/g).map((p, i) => p.startsWith('**') ? <b key={i}>{p.slice(2, -2)}</b> : p.startsWith('`') ? <code key={i} className="font-mono bg-[var(--surface)] rounded px-1 text-[12px]">{p.slice(1, -1)}</code> : <span key={i}>{p}</span>);
}
