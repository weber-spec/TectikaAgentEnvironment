'use client';

import React, { useRef, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import type { SortRule } from '@/lib/types';
import { Popover, Menu } from '@/components/ui/overlays';
import { Button, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { FilterBuilder } from './FilterBuilder';

export function Toolbar() {
  const { activeView, addTask, groups } = useBoard();
  const showGrouping = activeView.kind === 'table' || activeView.kind === 'kanban';
  const showColumns = activeView.kind === 'table';

  const handleNew = async () => {
    const first = groups[0];
    const gv = activeView.groupBy && first && first.key !== '__all__' ? { colId: activeView.groupBy, key: first.key } : undefined;
    const task = await addTask({ title: 'New item' }, gv);
    void task;
  };

  return (
    <div className="flex items-center gap-1.5 px-4 py-2 border-b border-[var(--border)] bg-[var(--background)] flex-wrap">
      <Button variant="primary" size="sm" onClick={handleNew}><Icon.plus size={15} /> New item</Button>
      <div className="w-px h-5 bg-[var(--border)] mx-1" />
      <SearchControl />
      <PersonFilter />
      <FilterControl />
      <SortControl />
      {showGrouping && <GroupControl />}
      {showColumns && <ColumnsControl />}
    </div>
  );
}

function ToolButton({ icon, label, active, badge, onClick, innerRef }: {
  icon: React.ReactNode; label: string; active?: boolean; badge?: number; onClick: () => void; innerRef?: React.RefObject<HTMLButtonElement | null>;
}) {
  return (
    <button ref={innerRef} onClick={onClick}
      className={`flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-[13px] font-medium transition-colors ${active ? 'bg-[var(--primary-light)] text-[var(--primary)]' : 'text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]'}`}>
      {icon}<span className="hidden sm:inline">{label}</span>
      {badge ? <span className="ml-0.5 min-w-[16px] h-4 px-1 rounded-full bg-[var(--primary)] text-white text-[10px] font-bold flex items-center justify-center">{badge}</span> : null}
    </button>
  );
}

function SearchControl() {
  const { search, setSearch } = useBoard();
  const [open, setOpen] = useState(false);
  return (
    <div className="flex items-center">
      {open || search ? (
        <div className="flex items-center gap-1 bg-[var(--surface)] rounded-md px-2 h-8 border border-[var(--border)]">
          <Icon.search size={14} className="text-[var(--muted)]" />
          <input autoFocus value={search} onChange={e => setSearch(e.target.value)} onBlur={() => !search && setOpen(false)}
            placeholder="Search…" className="bg-transparent outline-none text-sm w-36 text-[var(--foreground)]" />
          {search && <button onClick={() => setSearch('')} className="text-[var(--muted)]"><Icon.x size={13} /></button>}
        </div>
      ) : (
        <ToolButton icon={<Icon.search size={15} />} label="Search" onClick={() => setOpen(true)} />
      )}
    </div>
  );
}

function PersonFilter() {
  const { people, activeView, setFilter } = useBoard();
  const agents = people.filter(p => p.kind === 'Agent').slice(0, 4);
  const humans = people.filter(p => p.kind === 'Human').slice(0, 4);
  const shown = [...humans, ...agents].slice(0, 5);
  const activePerson = activeView.filter?.rules.find(r => r.columnId === 'people')?.value;

  const toggle = (name: string) => {
    if (activePerson === name) setFilter(undefined);
    else setFilter({ conjunction: 'and', rules: [{ id: 'pf', columnId: 'people', operator: 'is', value: name }] });
  };
  return (
    <div className="flex items-center pl-1">
      {shown.map((p, i) => (
        <button key={p.id} onClick={() => toggle(p.name)} title={`Filter: ${p.name}`}
          style={{ marginLeft: i === 0 ? 0 : -8, zIndex: shown.length - i, opacity: activePerson && activePerson !== p.name ? 0.4 : 1 }}
          className="rounded-full transition-all hover:-translate-y-0.5"
          >
          <Avatar person={p} size={26} ring />
        </button>
      ))}
    </div>
  );
}

function FilterControl() {
  const { activeView } = useBoard();
  const ref = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const count = activeView.filter?.rules.length ?? 0;
  return (
    <>
      <ToolButton innerRef={ref} icon={<Icon.filter size={15} />} label="Filter" active={count > 0} badge={count || undefined} onClick={() => setOpen(o => !o)} />
      <FilterBuilder anchorRef={ref} open={open} onClose={() => setOpen(false)} />
    </>
  );
}

function SortControl() {
  const { columns, activeView, setSorts } = useBoard();
  const ref = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const sorts = activeView.sorts ?? [];
  const setOne = (columnId: string, direction: SortRule['direction']) => {
    const others = sorts.filter(s => s.columnId !== columnId);
    setSorts([{ columnId, direction }, ...others]);
  };
  return (
    <>
      <ToolButton innerRef={ref} icon={<Icon.sort size={15} />} label="Sort" active={sorts.length > 0} badge={sorts.length || undefined} onClick={() => setOpen(o => !o)} />
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={280} className="p-3">
        <div className="flex items-center justify-between mb-2"><span className="text-sm font-semibold text-[var(--foreground)]">Sort by</span>{sorts.length > 0 && <button onClick={() => setSorts([])} className="text-xs text-[#e2445c] hover:underline">Clear</button>}</div>
        <div className="flex flex-col gap-1 max-h-[300px] overflow-auto">
          {columns.map(c => {
            const s = sorts.find(x => x.columnId === c.id);
            return (
              <div key={c.id} className="flex items-center gap-2 px-1 py-1 rounded hover:bg-[var(--surface)]">
                <span className="flex-1 text-sm text-[var(--foreground)] truncate">{c.title}</span>
                <button onClick={() => setOne(c.id, 'asc')} className={`p-1 rounded ${s?.direction === 'asc' ? 'bg-[var(--primary-light)] text-[var(--primary)]' : 'text-[var(--muted)]'}`}><Icon.arrowUp size={14} /></button>
                <button onClick={() => setOne(c.id, 'desc')} className={`p-1 rounded ${s?.direction === 'desc' ? 'bg-[var(--primary-light)] text-[var(--primary)]' : 'text-[var(--muted)]'}`}><Icon.arrowDown size={14} /></button>
              </div>
            );
          })}
        </div>
      </Popover>
    </>
  );
}

function GroupControl() {
  const { columns, activeView, setGroupBy } = useBoard();
  const ref = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const groupable = columns.filter(c => ['status', 'priority', 'people', 'dropdown', 'trigger'].includes(c.kind));
  const cur = columns.find(c => c.id === activeView.groupBy);
  return (
    <>
      <ToolButton innerRef={ref} icon={<Icon.group size={15} />} label={cur ? `Group: ${cur.title}` : 'Group'} active={!!activeView.groupBy} onClick={() => setOpen(o => !o)} />
      <Menu anchorRef={ref} open={open} onClose={() => setOpen(false)} width={200}
        options={[
          { label: 'No grouping', onClick: () => setGroupBy(undefined), checked: !activeView.groupBy },
          'divider',
          ...groupable.map(c => ({ label: c.title, onClick: () => setGroupBy(c.id), checked: activeView.groupBy === c.id })),
        ]} />
    </>
  );
}

function ColumnsControl() {
  const { columns, toggleColumnHidden } = useBoard();
  const ref = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const hiddenCount = columns.filter(c => c.hidden).length;
  return (
    <>
      <ToolButton innerRef={ref} icon={<Icon.eye size={15} />} label="Columns" active={hiddenCount > 0} onClick={() => setOpen(o => !o)} />
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={230} className="p-2">
        <div className="px-2 py-1 text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Show / hide columns</div>
        <div className="flex flex-col max-h-[320px] overflow-auto">
          {columns.map(c => (
            <button key={c.id} onClick={() => toggleColumnHidden(c.id)} className="flex items-center gap-2 px-2 py-1.5 rounded hover:bg-[var(--surface)] text-left">
              <span className="text-[var(--muted)]">{c.hidden ? <Icon.eyeOff size={15} /> : <Icon.eye size={15} />}</span>
              <span className={`flex-1 text-sm truncate ${c.hidden ? 'text-[var(--muted-2)] line-through' : 'text-[var(--foreground)]'}`}>{c.title}</span>
            </button>
          ))}
        </div>
      </Popover>
    </>
  );
}
