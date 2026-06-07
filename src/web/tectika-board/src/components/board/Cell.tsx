'use client';

import React, { useRef, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import type { AgentTask, ColumnDef, AgentTaskStatus, TaskPriority, Person } from '@/lib/types';
import { STATUS_CONFIG, STATUS_ORDER, PRIORITY_CONFIG, PRIORITY_ORDER, textOn, alpha } from '@/lib/palette';
import { cellText, cellNumber, evalFormula } from '@/lib/columns';
import { formatDateShort, daysUntil, displayName, fuzzyMatch } from '@/lib/format';
import { Popover } from '@/components/ui/overlays';
import { Avatar, Pill, Tag } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';

const CLEAR_DATE = '0001-01-01T00:00:00+00:00';

export function Cell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  switch (col.kind) {
    case 'title': return <TitleCell task={task} />;
    case 'status': return <StatusCell task={task} />;
    case 'priority': return <PriorityCell task={task} />;
    case 'people': return <PeopleCell task={task} />;
    case 'date': return <DateCell task={task} />;
    case 'timeline': return <TimelineCell task={task} />;
    case 'dependency': return <DependencyCell task={task} />;
    case 'upstream': return <DependencyChipsCell task={task} dir="up" />;
    case 'downstream': return <DependencyChipsCell task={task} dir="down" />;
    case 'checkbox': return <CheckboxCell task={task} col={col} />;
    case 'rating': return <RatingCell task={task} col={col} />;
    case 'progress': return <ProgressCell task={task} col={col} />;
    case 'number': return <NumberCell task={task} col={col} />;
    case 'text': return <TextCell task={task} col={col} />;
    case 'link': return <LinkCell task={task} col={col} />;
    case 'tags': return <TagsCell task={task} col={col} />;
    case 'dropdown': return <DropdownCell task={task} col={col} />;
    case 'formula': return <FormulaCell task={task} col={col} />;
    default: return <ReadonlyCell task={task} col={col} />;
  }
}

function CellWrap({ children, onClick, center }: { children: React.ReactNode; onClick?: (e: React.MouseEvent) => void; center?: boolean }) {
  return (
    <div onClick={onClick} className={`h-full w-full flex items-center ${center ? 'justify-center' : ''} px-2 cursor-default`}>
      {children}
    </div>
  );
}

// ── Title ───────────────────────────────────────────────────────────────────
function TitleCell({ task }: { task: AgentTask }) {
  const { openTask, updateTask } = useBoard();
  const [editing, setEditing] = useState(false);
  const [val, setVal] = useState(task.title);
  return (
    <div className="h-full w-full flex items-center px-2 gap-2 group/title">
      {editing ? (
        <input
          autoFocus value={val}
          onChange={e => setVal(e.target.value)}
          onBlur={() => { setEditing(false); if (val.trim() && val !== task.title) updateTask(task.id, { title: val.trim() }); else setVal(task.title); }}
          onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); if (e.key === 'Escape') { setVal(task.title); setEditing(false); } }}
          className="flex-1 text-sm bg-transparent outline-none border-b border-[var(--primary)] text-[var(--foreground)]"
        />
      ) : (
        <>
          <span className="flex-1 text-sm text-[var(--foreground)] truncate cursor-text" onDoubleClick={() => setEditing(true)}>{task.title}</span>
          <button onClick={() => openTask(task.id)}
            className="shrink-0 opacity-0 group-hover/title:opacity-100 transition-opacity flex items-center gap-1 text-[11px] text-[var(--muted)] hover:text-[var(--primary)] px-1.5 py-0.5 rounded hover:bg-[var(--surface)]">
            <Icon.edit size={12} /> Open
          </button>
        </>
      )}
    </div>
  );
}

// ── Label picker (status / priority / dropdown) ───────────────────────────────
function LabelCellBase<T extends string>({ value, config, order, onPick }: {
  value: T; config: Record<T, { label: string; hex: string }>; order: T[]; onPick: (v: T) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const cur = config[value];
  return (
    <div ref={ref} className="h-full w-full">
      <Pill full label={cur.label} hex={cur.hex} dropdown onClick={() => setOpen(o => !o)} />
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={200} className="p-2">
        <div className="flex flex-col gap-1">
          {order.map(v => (
            <button key={v} onClick={() => { onPick(v); setOpen(false); }}
              className="rounded px-2 py-1.5 text-[13px] font-semibold text-left transition-transform hover:scale-[1.02]"
              style={{ background: config[v].hex, color: textOn(config[v].hex) }}>
              {config[v].label}
            </button>
          ))}
        </div>
      </Popover>
    </div>
  );
}

function StatusCell({ task }: { task: AgentTask }) {
  const { setStatus } = useBoard();
  return <LabelCellBase value={task.status} config={STATUS_CONFIG} order={STATUS_ORDER} onPick={v => setStatus(task.id, v as AgentTaskStatus)} />;
}
function PriorityCell({ task }: { task: AgentTask }) {
  const { updateTask } = useBoard();
  return <LabelCellBase value={task.priority} config={PRIORITY_CONFIG} order={PRIORITY_ORDER} onPick={v => updateTask(task.id, { priority: v as TaskPriority })} />;
}

// ── People ────────────────────────────────────────────────────────────────────
function PeopleCell({ task }: { task: AgentTask }) {
  const { peopleById, people, updateTask } = useBoard();
  const ref = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const person: Person | undefined = peopleById[task.assignee.id];
  const filtered = people.filter(p => fuzzyMatch(p.name, q));
  return (
    <div ref={ref} className="h-full w-full">
      <CellWrap onClick={() => setOpen(o => !o)}>
        <div className="flex items-center gap-2 cursor-pointer">
          <Avatar person={person} name={displayName(task.assignee.id)} size={24} />
          <span className="text-xs text-[var(--muted)] truncate">{person?.name ?? displayName(task.assignee.id)}</span>
        </div>
      </CellWrap>
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={240} className="p-2">
        <input autoFocus placeholder="Search people…" value={q} onChange={e => setQ(e.target.value)}
          className="w-full text-sm px-2 py-1.5 rounded border border-[var(--border)] bg-[var(--surface)] outline-none mb-1" />
        <div className="max-h-[260px] overflow-auto flex flex-col">
          {filtered.map(p => (
            <button key={p.id} onClick={() => { updateTask(task.id, { assignee: { type: p.kind, id: p.id } }); setOpen(false); }}
              className="flex items-center gap-2 px-2 py-1.5 rounded hover:bg-[var(--surface)] text-left">
              <Avatar person={p} size={24} />
              <div className="min-w-0">
                <div className="text-[13px] text-[var(--foreground)] truncate">{p.name}</div>
                {p.title && <div className="text-[10px] text-[var(--muted)] truncate">{p.kind === 'Agent' ? `Agent · ${p.title}` : p.title}</div>}
              </div>
              {p.id === task.assignee.id && <span className="ml-auto text-[var(--primary)]">✓</span>}
            </button>
          ))}
          {filtered.length === 0 && <div className="px-2 py-3 text-xs text-[var(--muted)] text-center">No people found</div>}
        </div>
      </Popover>
    </div>
  );
}

// ── Date ────────────────────────────────────────────────────────────────────
function DateCell({ task }: { task: AgentTask }) {
  const { updateTask } = useBoard();
  const ref = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const dd = daysUntil(task.dueAt);
  const overdue = dd != null && dd < 0 && task.status !== 'Done';
  const soon = dd != null && dd >= 0 && dd <= 2 && task.status !== 'Done';
  const color = overdue ? '#e2445c' : soon ? '#fdab3d' : 'var(--muted)';
  return (
    <div ref={ref} className="h-full w-full">
      <CellWrap onClick={() => setOpen(o => !o)}>
        {task.dueAt ? (
          <span className="inline-flex items-center gap-1.5 text-xs cursor-pointer" style={{ color }}>
            <Icon.calendar size={13} />
            {formatDateShort(task.dueAt)}
            {dd != null && (overdue || soon) && <span className="text-[10px] font-semibold">{overdue ? `${-dd}d late` : dd === 0 ? 'today' : `${dd}d`}</span>}
          </span>
        ) : (
          <span className="text-xs text-[var(--muted-2)] cursor-pointer hover:text-[var(--muted)] inline-flex items-center gap-1"><Icon.calendar size={13} /> Set date</span>
        )}
      </CellWrap>
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={240} className="p-3">
        <input type="date" autoFocus
          value={task.dueAt ? new Date(task.dueAt).toISOString().slice(0, 10) : ''}
          onChange={e => { updateTask(task.id, { dueAt: e.target.value ? new Date(e.target.value).toISOString() : CLEAR_DATE }); }}
          className="w-full text-sm px-2 py-1.5 rounded border border-[var(--border)] bg-[var(--surface)] outline-none text-[var(--foreground)]" />
        <div className="flex gap-1 mt-2">
          {[['Today', 0], ['+1w', 7], ['+1m', 30]].map(([label, d]) => (
            <button key={label as string} onClick={() => { const dt = new Date(); dt.setDate(dt.getDate() + (d as number)); updateTask(task.id, { dueAt: dt.toISOString() }); setOpen(false); }}
              className="flex-1 text-xs py-1 rounded bg-[var(--surface)] hover:bg-[var(--primary-light)] text-[var(--muted)]">{label}</button>
          ))}
        </div>
        {task.dueAt && <button onClick={() => { updateTask(task.id, { dueAt: CLEAR_DATE }); setOpen(false); }} className="w-full mt-2 text-xs py-1 text-[#e2445c] hover:bg-[#e2445c11] rounded">Clear date</button>}
      </Popover>
    </div>
  );
}

function TimelineCell({ task }: { task: AgentTask }) {
  if (!task.dueAt) return <CellWrap><span className="text-xs text-[var(--muted-2)]">—</span></CellWrap>;
  const start = new Date(task.createdAt);
  const end = new Date(task.dueAt);
  const cfg = STATUS_CONFIG[task.status];
  return (
    <CellWrap>
      <div className="w-full">
        <div className="h-4 rounded-full flex items-center px-2 text-[10px] font-semibold" style={{ background: alpha(cfg.hex, 0.25), color: cfg.hex }}>
          <span className="truncate">{formatDateShort(start)} – {formatDateShort(end)}</span>
        </div>
      </div>
    </CellWrap>
  );
}

function DependencyCell({ task }: { task: AgentTask }) {
  const { openTask } = useBoard();
  const total = task.upstreamTaskIds.length + task.downstreamTaskIds.length;
  if (total === 0) return <CellWrap><span className="text-xs text-[var(--muted-2)]">—</span></CellWrap>;
  return (
    <CellWrap onClick={() => openTask(task.id)}>
      <span className="inline-flex items-center gap-1.5 text-xs text-[var(--primary)] font-medium cursor-pointer">
        <Icon.link size={13} />
        {task.upstreamTaskIds.length > 0 && <span title="upstream">↑{task.upstreamTaskIds.length}</span>}
        {task.downstreamTaskIds.length > 0 && <span title="downstream">↓{task.downstreamTaskIds.length}</span>}
      </span>
    </CellWrap>
  );
}

// ── Explicit upstream / downstream dependency cells ───────────────────────────
// `up`  = tasks that feed INTO this task (Upstream Input)
// `down` = tasks this task routes its output TO (Downstream Target)
function DependencyChipsCell({ task, dir }: { task: AgentTask; dir: 'up' | 'down' }) {
  const { tasks, openTask, connectTasks, disconnectTasks } = useBoard();
  const addRef = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const ids = dir === 'up' ? task.upstreamTaskIds : task.downstreamTaskIds;
  const linked = ids.map(id => tasks.find(t => t.id === id)).filter((t): t is AgentTask => !!t);
  const candidates = tasks.filter(t => t.id !== task.id && !ids.includes(t.id));
  const remove = (otherId: string) => dir === 'up' ? disconnectTasks(otherId, task.id) : disconnectTasks(task.id, otherId);
  const add = (otherId: string) => { if (dir === 'up') connectTasks(otherId, task.id); else connectTasks(task.id, otherId); setOpen(false); };

  return (
    <div className="h-full w-full flex items-center gap-1 px-1.5 overflow-hidden group/dep">
      <div className="flex items-center gap-1 overflow-hidden flex-1 min-w-0">
        {linked.length === 0 && <span className="text-xs text-[var(--muted-2)]">—</span>}
        {linked.slice(0, 2).map(t => <DepChip key={t.id} task={t} onOpen={() => openTask(t.id)} onRemove={() => remove(t.id)} />)}
        {linked.length > 2 && <span className="text-[10px] text-[var(--muted)] shrink-0" title={linked.slice(2).map(t => t.title).join(', ')}>+{linked.length - 2}</span>}
      </div>
      <button ref={addRef} onClick={() => setOpen(o => !o)} title={dir === 'up' ? 'Link an upstream input' : 'Link a downstream target'}
        className="shrink-0 w-5 h-5 flex items-center justify-center rounded text-[var(--muted-2)] opacity-0 group-hover/dep:opacity-100 hover:text-[var(--primary)] hover:bg-[var(--surface)]"><Icon.plus size={13} /></button>
      <Popover anchorRef={addRef} open={open} onClose={() => setOpen(false)} width={250} className="p-1.5">
        <DepPicker candidates={candidates} dir={dir} onPick={add} />
      </Popover>
    </div>
  );
}

function DepChip({ task, onOpen, onRemove }: { task: AgentTask; onOpen: () => void; onRemove: () => void }) {
  const st = STATUS_CONFIG[task.status];
  return (
    <span className="group/chip inline-flex items-center gap-1 pl-1.5 pr-1 py-0.5 rounded text-[11px] font-medium max-w-[130px] cursor-pointer shrink-0"
      style={{ background: alpha(st.hex, 0.15), color: st.hex }} onClick={onOpen} title={`${task.title} · ${st.label}`}>
      <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: st.hex }} />
      <span className="truncate">{task.title}</span>
      <button onClick={e => { e.stopPropagation(); onRemove(); }} className="opacity-0 group-hover/chip:opacity-100 hover:text-[#e2445c] shrink-0" title="Unlink"><Icon.x size={10} /></button>
    </span>
  );
}

function DepPicker({ candidates, dir, onPick }: { candidates: AgentTask[]; dir: 'up' | 'down'; onPick: (id: string) => void }) {
  const [q, setQ] = useState('');
  const filtered = candidates.filter(t => fuzzyMatch(t.title, q)).slice(0, 50);
  return (
    <div className="flex flex-col" onClick={e => e.stopPropagation()}>
      <input autoFocus value={q} onChange={e => setQ(e.target.value)} placeholder={dir === 'up' ? 'Link an upstream task…' : 'Link a downstream task…'}
        className="inp !text-xs !py-1.5 mb-1" />
      <div className="max-h-56 overflow-auto flex flex-col gap-0.5">
        {filtered.length === 0 && <div className="text-xs text-[var(--muted)] px-2 py-3 text-center">No matching tasks</div>}
        {filtered.map(t => {
          const st = STATUS_CONFIG[t.status];
          return (
            <button key={t.id} onClick={() => onPick(t.id)} className="w-full flex items-center gap-2 px-2 py-1.5 rounded hover:bg-[var(--surface)] text-left text-[13px]">
              <span className="w-2 h-2 rounded-full shrink-0" style={{ background: st.hex }} />
              <span className="truncate text-[var(--foreground)] flex-1">{t.title}</span>
              <span className="text-[10px] shrink-0" style={{ color: st.hex }}>{st.label}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

// ── Custom-value cells ────────────────────────────────────────────────────────
function CheckboxCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const checked = cellContext.customCells[task.id]?.[col.id] === 'true';
  return (
    <CellWrap center>
      <input type="checkbox" checked={checked} onChange={e => setCustomCell(task.id, col.id, String(e.target.checked))}
        className="w-4 h-4 rounded accent-[var(--primary)] cursor-pointer" />
    </CellWrap>
  );
}

function RatingCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const val = Number(cellContext.customCells[task.id]?.[col.id] ?? 0);
  return (
    <CellWrap center>
      <div className="flex gap-0.5">
        {[1, 2, 3, 4, 5].map(n => (
          <button key={n} onClick={() => setCustomCell(task.id, col.id, String(n === val ? 0 : n))} className="transition-transform hover:scale-110"
            style={{ color: n <= val ? '#fdab3d' : 'var(--muted-2)' }}>
            <Icon.star size={14} strokeWidth={n <= val ? 0 : 1.8} className={n <= val ? 'fill-current' : ''} />
          </button>
        ))}
      </div>
    </CellWrap>
  );
}

function ProgressCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const ref = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const val = Math.max(0, Math.min(100, Number(cellContext.customCells[task.id]?.[col.id] ?? 0)));
  const hex = val >= 100 ? '#00c875' : val >= 50 ? '#fdab3d' : '#0086c0';
  return (
    <div ref={ref} className="h-full w-full">
      <CellWrap onClick={() => setOpen(o => !o)}>
        <div className="w-full flex items-center gap-2 cursor-pointer">
          <div className="flex-1 h-3.5 rounded-full overflow-hidden bg-[var(--surface-2)]">
            <div className="h-full rounded-full transition-all" style={{ width: `${val}%`, background: hex }} />
          </div>
          <span className="text-[11px] text-[var(--muted)] w-8 text-right">{val}%</span>
        </div>
      </CellWrap>
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={200} className="p-3">
        <input type="range" min={0} max={100} step={5} value={val} onChange={e => setCustomCell(task.id, col.id, e.target.value)} className="w-full accent-[var(--primary)]" />
        <div className="text-center text-sm font-semibold text-[var(--foreground)] mt-1">{val}%</div>
      </Popover>
    </div>
  );
}

function NumberCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const v = cellContext.customCells[task.id]?.[col.id] ?? '';
  return (
    <input type="number" value={v} placeholder="—" onChange={e => setCustomCell(task.id, col.id, e.target.value)}
      className="h-full w-full bg-transparent outline-none px-2 text-sm text-right text-[var(--foreground)] focus:bg-[var(--primary-light)]" />
  );
}

function TextCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const v = cellContext.customCells[task.id]?.[col.id] ?? '';
  return (
    <input value={v} placeholder="—" onChange={e => setCustomCell(task.id, col.id, e.target.value)}
      className="h-full w-full bg-transparent outline-none px-2 text-sm text-[var(--foreground)] focus:bg-[var(--primary-light)]" />
  );
}

function LinkCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const [editing, setEditing] = useState(false);
  const v = cellContext.customCells[task.id]?.[col.id] ?? '';
  if (editing || !v) return (
    <input autoFocus={editing} value={v} placeholder="Paste link…" onChange={e => setCustomCell(task.id, col.id, e.target.value)} onBlur={() => setEditing(false)}
      className="h-full w-full bg-transparent outline-none px-2 text-sm text-[var(--primary)] focus:bg-[var(--primary-light)]" />
  );
  return (
    <CellWrap>
      <a href={v.startsWith('http') ? v : `https://${v}`} target="_blank" rel="noreferrer" onClick={e => e.stopPropagation()}
        onDoubleClick={() => setEditing(true)}
        className="inline-flex items-center gap-1 text-xs text-[var(--primary)] hover:underline truncate"><Icon.link size={12} />{v.replace(/^https?:\/\//, '')}</a>
    </CellWrap>
  );
}

function TagsCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const raw = cellContext.customCells[task.id]?.[col.id] ?? '';
  const tags = raw.split(',').map(s => s.trim()).filter(Boolean);
  const [editing, setEditing] = useState(false);
  if (editing) return (
    <input autoFocus value={raw} placeholder="tag1, tag2" onChange={e => setCustomCell(task.id, col.id, e.target.value)} onBlur={() => setEditing(false)}
      className="h-full w-full bg-transparent outline-none px-2 text-sm text-[var(--foreground)]" />
  );
  return (
    <CellWrap onClick={() => setEditing(true)}>
      <div className="flex items-center gap-1 flex-wrap cursor-text">
        {tags.length ? tags.map((tg, i) => <Tag key={i} label={tg} hex={['#0086c0', '#a25ddc', '#00c875', '#fdab3d'][i % 4]} />) : <span className="text-xs text-[var(--muted-2)]">—</span>}
      </div>
    </CellWrap>
  );
}

function DropdownCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext, setCustomCell } = useBoard();
  const ref = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const v = cellContext.customCells[task.id]?.[col.id] ?? '';
  const opts = col.options ?? [{ label: 'Option A', hex: '#0086c0' }, { label: 'Option B', hex: '#a25ddc' }, { label: 'Option C', hex: '#00c875' }];
  const cur = opts.find(o => o.label === v);
  return (
    <div ref={ref} className="h-full w-full">
      {cur ? <Pill full label={cur.label} hex={cur.hex} dropdown onClick={() => setOpen(o => !o)} />
        : <CellWrap onClick={() => setOpen(o => !o)}><span className="text-xs text-[var(--muted-2)] cursor-pointer">Select…</span></CellWrap>}
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={180} className="p-2 flex flex-col gap-1">
        {opts.map(o => (
          <button key={o.label} onClick={() => { setCustomCell(task.id, col.id, o.label); setOpen(false); }}
            className="rounded px-2 py-1.5 text-[13px] font-semibold text-left" style={{ background: o.hex, color: textOn(o.hex) }}>{o.label}</button>
        ))}
      </Popover>
    </div>
  );
}

function FormulaCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext } = useBoard();
  const n = evalFormula(col, task, cellContext);
  return <CellWrap center><span className="text-sm text-[var(--foreground)] font-medium">{n == null ? '—' : n}</span></CellWrap>;
}

function ReadonlyCell({ task, col }: { task: AgentTask; col: ColumnDef }) {
  const { cellContext } = useBoard();
  const text = cellText(task, col, cellContext);
  const num = cellNumber(task, col, cellContext);
  void num;
  return <CellWrap><span className="text-xs text-[var(--muted)] truncate">{text || '—'}</span></CellWrap>;
}
