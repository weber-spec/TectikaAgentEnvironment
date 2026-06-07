'use client';

import React, { useEffect, useState, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useBoard } from '@/lib/board-context';
import { api } from '@/lib/api';
import type { Artifact, AgentTask } from '@/lib/types';
import { STATUS_CONFIG, STATUS_ORDER, PRIORITY_CONFIG, PRIORITY_ORDER, textOn } from '@/lib/palette';
import { Avatar, Pill, Button, Spinner } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { Popover } from '@/components/ui/overlays';
import { formatDateTime, relativeTime, displayName } from '@/lib/format';

type Tab = 'updates' | 'activity' | 'details';

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
  const [tab, setTab] = useState<Tab>('updates');

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
        <button onClick={() => openTask(undefined)} className="w-8 h-8 flex items-center justify-center rounded-md text-[var(--muted)] hover:bg-[var(--surface)] shrink-0"><Icon.x size={18} /></button>
      </div>

      {/* dual-pane body */}
      <div className="flex-1 flex min-h-0">
        {/* left: execution thread / config */}
        <div className="w-[420px] shrink-0 border-r border-[var(--border)] flex flex-col min-h-0">
          <div className="flex border-b border-[var(--border)] px-2">
            {(['updates', 'activity', 'details'] as Tab[]).map(t => (
              <button key={t} onClick={() => setTab(t)} className={`px-3 py-2.5 text-[13px] font-medium capitalize border-b-2 -mb-px transition-colors ${tab === t ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>{t}</button>
            ))}
          </div>
          <div className="flex-1 overflow-auto">
            {tab === 'updates' && <UpdatesTab task={task} />}
            {tab === 'activity' && <ActivityTab task={task} />}
            {tab === 'details' && <DetailsTab task={task} role={role} run={run} people={people} onAssign={(id, kind) => updateTask(task.id, { assignee: { type: kind, id } })} />}
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

// ── Updates (comments) ────────────────────────────────────────────────────────
function UpdatesTab({ task }: { task: AgentTask }) {
  const { comments, addComment, peopleById } = useBoard();
  const [draft, setDraft] = useState('');
  const list = comments.filter(c => c.taskId === task.id).sort((a, b) => +new Date(a.createdAt) - +new Date(b.createdAt));
  return (
    <div className="flex flex-col h-full">
      <div className="flex-1 overflow-auto p-4 flex flex-col gap-3">
        {list.length === 0 && <p className="text-sm text-[var(--muted)] text-center mt-8">No updates yet. Start the conversation.</p>}
        {list.map(c => {
          const p = peopleById[c.authorId];
          return (
            <div key={c.id} className="flex gap-2.5">
              <Avatar person={p} name={c.authorId} size={30} />
              <div className="flex-1 min-w-0">
                <div className="flex items-baseline gap-2"><span className="text-[13px] font-semibold text-[var(--foreground)]">{p?.name ?? displayName(c.authorId)}</span><span className="text-[11px] text-[var(--muted)]">{relativeTime(c.createdAt)}</span></div>
                <div className="text-[13px] text-[var(--foreground)] mt-0.5 whitespace-pre-wrap break-words">{renderMentions(c.body)}</div>
                {c.reactions && Object.entries(c.reactions).map(([emo, who]) => <span key={emo} className="inline-flex items-center gap-1 text-xs bg-[var(--surface)] rounded-full px-2 py-0.5 mt-1 mr-1">{emo} {who.length}</span>)}
              </div>
            </div>
          );
        })}
      </div>
      <div className="border-t border-[var(--border)] p-3">
        <textarea value={draft} onChange={e => setDraft(e.target.value)} placeholder="Write an update… use @ to mention"
          onKeyDown={e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { if (draft.trim()) { addComment(task.id, draft.trim()); setDraft(''); } } }}
          className="w-full text-sm bg-[var(--surface)] rounded-lg p-2.5 outline-none resize-none text-[var(--foreground)] border border-[var(--border)] focus:border-[var(--primary)]" rows={2} />
        <div className="flex justify-between items-center mt-2">
          <span className="text-[11px] text-[var(--muted-2)]">⌘+Enter to send</span>
          <Button variant="primary" size="sm" disabled={!draft.trim()} onClick={() => { addComment(task.id, draft.trim()); setDraft(''); }}><Icon.send size={13} /> Update</Button>
        </div>
      </div>
    </div>
  );
}

function renderMentions(body: string) {
  return body.split(/(@[\w.@-]+)/g).map((part, i) => part.startsWith('@')
    ? <span key={i} className="text-[var(--primary)] font-medium bg-[var(--primary-light)] rounded px-1">{part}</span>
    : <span key={i}>{part}</span>);
}

// ── Activity ──────────────────────────────────────────────────────────────────
function ActivityTab({ task }: { task: AgentTask }) {
  const { activity, peopleById } = useBoard();
  const list = activity.filter(a => a.taskId === task.id).sort((a, b) => +new Date(b.createdAt) - +new Date(a.createdAt));
  const verb: Record<string, string> = { created: 'created this item', status: 'changed status', priority: 'changed priority', assignee: 'reassigned', due: 'set due date', connected: 'linked a dependency', comment: 'commented', artifact: 'updated the artifact', approval: 'requested approval', field: 'updated a field' };
  return (
    <div className="p-4 flex flex-col gap-3">
      {list.map(a => {
        const p = peopleById[a.actorId];
        return (
          <div key={a.id} className="flex gap-2.5 items-start">
            <Avatar person={p} name={a.actorId} size={26} />
            <div className="flex-1 text-[13px]">
              <span className="font-semibold text-[var(--foreground)]">{p?.name ?? displayName(a.actorId)}</span>{' '}
              <span className="text-[var(--muted)]">{verb[a.kind] ?? a.kind}</span>
              {a.from && a.to && <span className="text-[var(--muted)]"> from <b className="text-[var(--foreground)]">{a.from}</b> to <b className="text-[var(--foreground)]">{a.to}</b></span>}
              <div className="text-[11px] text-[var(--muted-2)]">{relativeTime(a.createdAt)}</div>
            </div>
          </div>
        );
      })}
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
      <Field label="Owner">
        <select value={task.assignee.id} onChange={e => { const p = people.find(x => x.id === e.target.value); onAssign(e.target.value, p?.kind ?? 'Human'); }}
          className="w-full bg-[var(--surface)] rounded-lg p-2 outline-none text-[var(--foreground)] border border-[var(--border)]">
          <optgroup label="Agents">{people.filter(p => p.kind === 'Agent').map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</optgroup>
          <optgroup label="People">{people.filter(p => p.kind === 'Human').map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</optgroup>
        </select>
      </Field>
      {role && (
        <div className="rounded-lg border border-[var(--border)] p-3 bg-[var(--background)]">
          <div className="flex items-center gap-2 mb-2"><Icon.robot size={16} className="text-[var(--primary)]" /><span className="font-semibold text-[var(--foreground)]">Agent configuration</span></div>
          <Field label="System prompt"><div className="text-[12px] text-[var(--muted)] bg-[var(--surface)] rounded p-2 max-h-28 overflow-auto whitespace-pre-wrap">{role.systemPrompt}</div></Field>
          <div className="flex gap-4 mt-2 text-xs">
            <div><span className="text-[var(--muted)]">Model</span><div className="font-medium text-[var(--foreground)]">{role.modelOverride ?? 'default'}</div></div>
            <div><span className="text-[var(--muted)]">Tools</span><div className="font-medium text-[var(--foreground)]">{role.tools.join(', ') || '—'}</div></div>
          </div>
        </div>
      )}
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
    </div>
  );
}
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <div><div className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold mb-1">{label}</div>{children}</div>;
}
function Stat({ label, value }: { label: string; value: string }) {
  return <div className="bg-[var(--surface)] rounded-lg py-2"><div className="text-[10px] uppercase text-[var(--muted)]">{label}</div><div className="text-sm font-semibold text-[var(--foreground)]">{value}</div></div>;
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
