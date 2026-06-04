'use client';

import React, { useRef, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import type { AgentTask, ColumnDef, ColumnAggregation } from '@/lib/types';
import type { TaskGroup } from '@/lib/board-engine';
import { aggregate, KIND_META } from '@/lib/columns';
import { Cell } from '@/components/board/Cell';
import { Popover } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { AddColumnButton } from './AddColumnButton';

const GUTTER = 78; // drag handle + checkbox + expand

export function TableView() {
  const { groups, visibleColumns, collapsedGroups, activeView } = useBoard();
  const totalWidth = GUTTER + visibleColumns.reduce((s, c) => s + c.width, 0) + 140;

  return (
    <div className="h-full overflow-auto">
      <div style={{ minWidth: totalWidth }}>
        <HeaderRow />
        {groups.map(g => (
          <GroupSection key={g.key} group={g} collapsed={collapsedGroups.includes(g.key)} grouped={!!activeView.groupBy} />
        ))}
        <NewGroupItem />
      </div>
    </div>
  );
}

function HeaderRow() {
  const { visibleColumns } = useBoard();
  return (
    <div className="flex sticky top-0 z-20 bg-[var(--surface)] border-b border-[var(--border)] h-9 select-none">
      <div style={{ width: GUTTER }} className="shrink-0 border-r border-[var(--border)]" />
      {visibleColumns.map(col => <HeaderCell key={col.id} col={col} />)}
      <div className="flex items-center px-2 border-r border-[var(--border)]"><AddColumnButton /></div>
    </div>
  );
}

function HeaderCell({ col }: { col: ColumnDef }) {
  const { resizeColumn, removeColumn, toggleColumnHidden, setSorts, activeView, setAggregation, renameColumn } = useBoardExtra();
  const ref = useRef<HTMLDivElement>(null);
  const [menu, setMenu] = useState(false);
  const startX = useRef(0); const startW = useRef(0);

  const onResizeDown = (e: React.PointerEvent) => {
    e.preventDefault(); e.stopPropagation();
    startX.current = e.clientX; startW.current = col.width;
    const move = (ev: PointerEvent) => resizeColumn(col.id, startW.current + (ev.clientX - startX.current));
    const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up); };
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', up);
  };

  const sortDir = activeView.sorts?.find(s => s.columnId === col.id)?.direction;
  const aggOptions: ColumnAggregation[] = KIND_META[col.kind].domain === 'number'
    ? ['none', 'sum', 'avg', 'min', 'max', 'median']
    : ['none', 'count', 'countEmpty', 'distribution'];

  return (
    <div ref={ref} style={{ width: col.width }} className="shrink-0 relative group/h flex items-center border-r border-[var(--border)]">
      <button onClick={() => setMenu(m => !m)} className="flex-1 flex items-center gap-1.5 px-2 h-full text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)] hover:text-[var(--foreground)] min-w-0">
        <span className="truncate">{col.title}</span>
        {sortDir && (sortDir === 'asc' ? <Icon.arrowUp size={11} /> : <Icon.arrowDown size={11} />)}
        <Icon.chevronDown size={11} className="ml-auto opacity-0 group-hover/h:opacity-60" />
      </button>
      <div onPointerDown={onResizeDown} className="absolute right-0 top-0 h-full w-1.5 cursor-col-resize hover:bg-[var(--primary)] opacity-0 group-hover/h:opacity-100 transition-opacity" />
      <Popover anchorRef={ref} open={menu} onClose={() => setMenu(false)} width={200} className="py-1">
        <MenuRow icon={<Icon.arrowUp size={15} />} label="Sort ascending" onClick={() => { setSorts([{ columnId: col.id, direction: 'asc' }]); setMenu(false); }} />
        <MenuRow icon={<Icon.arrowDown size={15} />} label="Sort descending" onClick={() => { setSorts([{ columnId: col.id, direction: 'desc' }]); setMenu(false); }} />
        <Divider />
        <RenameRow col={col} onRename={renameColumn} onDone={() => setMenu(false)} />
        <div className="px-3 py-1.5 text-[11px] text-[var(--muted)] uppercase tracking-wide">Summary</div>
        {aggOptions.map(a => (
          <MenuRow key={a} label={a === 'none' ? 'None' : a[0].toUpperCase() + a.slice(1)} checked={(col.aggregation ?? 'none') === a} onClick={() => { setAggregation(col.id, a); setMenu(false); }} />
        ))}
        <Divider />
        <MenuRow icon={<Icon.eyeOff size={15} />} label="Hide column" onClick={() => { toggleColumnHidden(col.id); setMenu(false); }} />
        {col.custom && <MenuRow icon={<Icon.trash size={15} />} label="Delete column" danger onClick={() => { removeColumn(col.id); setMenu(false); }} />}
      </Popover>
    </div>
  );
}

function RenameRow({ col, onRename, onDone }: { col: ColumnDef; onRename: (id: string, t: string) => void; onDone: () => void }) {
  const [v, setV] = useState(col.title);
  return (
    <div className="px-3 py-1.5">
      <input value={v} onChange={e => setV(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter') { onRename(col.id, v.trim() || col.title); onDone(); } }}
        className="w-full text-sm px-2 py-1 rounded border border-[var(--border)] bg-[var(--surface)] outline-none" />
    </div>
  );
}

function GroupSection({ group, collapsed, grouped }: { group: TaskGroup; collapsed: boolean; grouped: boolean }) {
  const { toggleGroup } = useBoard();
  return (
    <div className="mb-1.5">
      {grouped && (
        <div className="flex items-center sticky left-0 gap-2 px-2 h-9" style={{ width: 'fit-content' }}>
          <button onClick={() => toggleGroup(group.key)} className="flex items-center gap-1.5" style={{ color: group.hex }}>
            <Icon.chevronDown size={16} className={`transition-transform ${collapsed ? '-rotate-90' : ''}`} />
            <span className="font-bold text-sm">{group.label}</span>
          </button>
          <span className="text-xs text-[var(--muted)]">{group.tasks.length} item{group.tasks.length !== 1 ? 's' : ''}</span>
        </div>
      )}
      {!collapsed && (
        <div className="border-t border-l-4 rounded-tl" style={{ borderLeftColor: group.hex, borderTopColor: 'var(--border)' }}>
          {group.tasks.map(task => <Row key={task.id} task={task} accent={group.hex} />)}
          <AddItemRow group={group} accent={group.hex} />
          <GroupFooter group={group} accent={group.hex} />
        </div>
      )}
    </div>
  );
}

function Row({ task, accent }: { task: AgentTask; accent: string }) {
  const { visibleColumns, selectedIds, toggleSelect, openTask } = useBoard();
  const selected = selectedIds.includes(task.id);
  void accent;
  return (
    <div className={`flex border-b border-[var(--border)] h-10 group/row transition-colors ${selected ? 'bg-[var(--primary-light)]' : 'hover:bg-[var(--surface)]'}`}>
      <div style={{ width: GUTTER }} className="shrink-0 flex items-center gap-1 pl-1 border-r border-[var(--border)]">
        <span className="w-4 text-[var(--muted-2)] opacity-0 group-hover/row:opacity-100 cursor-grab"><Icon.drag size={14} /></span>
        <input type="checkbox" checked={selected} onChange={e => toggleSelect(task.id, (e.nativeEvent as MouseEvent).shiftKey)} className="w-4 h-4 rounded accent-[var(--primary)] cursor-pointer" />
        <button onClick={() => openTask(task.id)} className="w-5 text-[var(--muted-2)] opacity-0 group-hover/row:opacity-100 hover:text-[var(--primary)]" title="Open"><Icon.edit size={12} /></button>
      </div>
      {visibleColumns.map(col => (
        <div key={col.id} style={{ width: col.width }} className="shrink-0 border-r border-[var(--border)] overflow-hidden">
          <Cell task={task} col={col} />
        </div>
      ))}
    </div>
  );
}

function AddItemRow({ group, accent }: { group: TaskGroup; accent: string }) {
  void accent;
  const { addTask, activeView, visibleColumns } = useBoard();
  const [val, setVal] = useState('');
  const totalW = GUTTER + visibleColumns.reduce((s, c) => s + c.width, 0);
  const submit = () => {
    const title = val.trim();
    if (!title) return;
    const gv = activeView.groupBy && group.key !== '__all__' ? { colId: activeView.groupBy, key: group.key } : undefined;
    addTask({ title }, gv);
    setVal('');
  };
  return (
    <div className="flex h-9 border-b border-[var(--border)] items-center" style={{ width: totalW }}>
      <div style={{ width: GUTTER }} className="shrink-0 flex items-center justify-center text-[var(--muted-2)]"><Icon.plus size={15} /></div>
      <input value={val} onChange={e => setVal(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') submit(); }} onBlur={submit}
        placeholder="+ Add item" className="flex-1 bg-transparent outline-none text-sm px-2 placeholder:text-[var(--muted-2)] text-[var(--foreground)]" />
    </div>
  );
}

function GroupFooter({ group, accent }: { group: TaskGroup; accent: string }) {
  const { visibleColumns, cellContext } = useBoard();
  void accent;
  return (
    <div className="flex h-8 bg-[var(--surface)]/40">
      <div style={{ width: GUTTER }} className="shrink-0 border-r border-[var(--border)]" />
      {visibleColumns.map(col => {
        const agg = aggregate(group.tasks, col, cellContext);
        return (
          <div key={col.id} style={{ width: col.width }} className="shrink-0 border-r border-[var(--border)] px-2 flex flex-col justify-center overflow-hidden">
            {agg && (agg.distribution ? (
              <div className="flex h-2.5 rounded-full overflow-hidden mt-1" title={agg.distribution.map(d => `${d.label}: ${d.count}`).join(', ')}>
                {agg.distribution.map(d => <div key={d.label} style={{ flex: d.count, background: d.hex }} />)}
              </div>
            ) : (
              <>
                <span className="text-[9px] uppercase tracking-wide text-[var(--muted-2)] leading-none">{agg.label}</span>
                <span className="text-xs font-semibold text-[var(--foreground)] leading-tight">{agg.value}</span>
              </>
            ))}
          </div>
        );
      })}
    </div>
  );
}

function NewGroupItem() {
  const { tasks } = useBoard();
  if (tasks.length > 0) return null;
  return <div className="p-4" />;
}

// helpers
function MenuRow({ icon, label, onClick, danger, checked }: { icon?: React.ReactNode; label: string; onClick: () => void; danger?: boolean; checked?: boolean }) {
  return (
    <button onClick={onClick} className={`w-full flex items-center gap-2.5 px-3 py-1.5 text-left text-[13px] ${danger ? 'text-[#e2445c] hover:bg-[#e2445c11]' : 'text-[var(--foreground)] hover:bg-[var(--surface)]'}`}>
      {icon && <span className="text-[var(--muted)]">{icon}</span>}
      <span className="flex-1 truncate">{label}</span>
      {checked && <span className="text-[var(--primary)]">✓</span>}
    </button>
  );
}
function Divider() { return <div className="my-1 border-t border-[var(--border)]" />; }

// the board context augmented with renameColumn (added inline here to avoid editing context for a tiny helper)
function useBoardExtra() {
  const b = useBoard();
  const renameColumn = (id: string, title: string) => b.setColumns(b.columns.map(c => c.id === id ? { ...c, title } : c));
  return { ...b, renameColumn };
}
