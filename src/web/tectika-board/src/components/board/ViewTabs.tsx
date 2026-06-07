'use client';

import React, { useRef, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import type { ViewKind } from '@/lib/types';
import { Menu } from '@/components/ui/overlays';
import { Icon, type IconName } from '@/components/ui/icons';

const KIND_ICON: Record<ViewKind, IconName> = {
  table: 'table', kanban: 'kanban', timeline: 'timeline', calendar: 'calendar', cards: 'cards', chart: 'chart', canvas: 'flow',
};
const KIND_LABEL: Record<ViewKind, string> = {
  table: 'Table', kanban: 'Kanban', timeline: 'Timeline', calendar: 'Calendar', cards: 'Cards', chart: 'Chart', canvas: 'Canvas',
};

export function ViewTabs() {
  const { views, activeView, setActiveView, createView } = useBoard();
  const addRef = useRef<HTMLButtonElement>(null);
  const [addOpen, setAddOpen] = useState(false);

  return (
    <div className="flex items-center gap-0.5 px-3 border-b border-[var(--border)] bg-[var(--background)] overflow-x-auto">
      {views.map(v => {
        const I = Icon[KIND_ICON[v.kind]];
        const active = v.id === activeView.id;
        return (
          <ViewTab key={v.id} active={active} icon={<I size={15} />} name={v.name} viewId={v.id} onClick={() => setActiveView(v.id)} />
        );
      })}
      <button ref={addRef} onClick={() => setAddOpen(o => !o)} className="flex items-center gap-1 px-2 py-2 text-[13px] text-[var(--muted)] hover:text-[var(--primary)] shrink-0"><Icon.plus size={15} /></button>
      <Menu anchorRef={addRef} open={addOpen} onClose={() => setAddOpen(false)} width={190}
        header={<div className="px-3 py-1.5 text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Add view</div>}
        options={(['table', 'kanban', 'timeline', 'calendar', 'cards', 'chart', 'canvas'] as ViewKind[]).map(k => {
          const I = Icon[KIND_ICON[k]];
          return { label: KIND_LABEL[k], icon: <I size={15} />, onClick: () => createView(KIND_LABEL[k], k) };
        })} />
    </div>
  );
}

function ViewTab({ active, icon, name, viewId, onClick }: { active: boolean; icon: React.ReactNode; name: string; viewId: string; onClick: () => void }) {
  const { renameView, deleteView, views } = useBoard();
  const ref = useRef<HTMLDivElement>(null);
  const [menu, setMenu] = useState(false);
  const [editing, setEditing] = useState(false);
  const [v, setV] = useState(name);
  const canDelete = views.length > 1;

  return (
    <div ref={ref} className="relative shrink-0 group/tab">
      <button onClick={onClick} onDoubleClick={() => { setV(name); setEditing(true); }} onContextMenu={e => { e.preventDefault(); setMenu(true); }}
        className={`flex items-center gap-1.5 px-3 py-2.5 text-[13px] font-medium border-b-2 -mb-px transition-colors ${active ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
        {icon}
        {editing ? (
          <input autoFocus value={v} onChange={e => setV(e.target.value)} onClick={e => e.stopPropagation()}
            onBlur={() => { setEditing(false); if (v.trim()) renameView(viewId, v.trim()); }}
            onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
            className="bg-transparent outline-none border-b border-[var(--primary)] w-20" />
        ) : name}
        {active && <span onClick={e => { e.stopPropagation(); setMenu(m => !m); }} className="opacity-0 group-hover/tab:opacity-100"><Icon.chevronDown size={12} /></span>}
      </button>
      <Menu anchorRef={ref} open={menu} onClose={() => setMenu(false)} width={160}
        options={[
          { label: 'Rename', icon: <Icon.edit size={14} />, onClick: () => { setV(name); setEditing(true); } },
          ...(canDelete ? [{ label: 'Delete view', icon: <Icon.trash size={14} />, danger: true, onClick: () => deleteView(viewId) }] : []),
        ]} />
    </div>
  );
}
