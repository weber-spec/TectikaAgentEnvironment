'use client';

import React, { useRef, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import { STATUS_CONFIG, STATUS_ORDER, PRIORITY_CONFIG, PRIORITY_ORDER, textOn } from '@/lib/palette';
import { Popover } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';

export function BatchToolbar() {
  const { selectedIds, clearSelection, updateTask, setStatus, deleteTasks } = useBoard();
  const statusRef = useRef<HTMLButtonElement>(null);
  const prioRef = useRef<HTMLButtonElement>(null);
  const [sOpen, setSOpen] = useState(false);
  const [pOpen, setPOpen] = useState(false);
  if (selectedIds.length === 0) return null;

  return (
    <div className="absolute bottom-5 left-1/2 -translate-x-1/2 z-30 flex items-center bg-[#323338] text-white rounded-xl shadow-2xl overflow-hidden animate-slide-in-panel">
      <div className="flex items-center gap-2 px-4 py-2.5 border-r border-white/10">
        <span className="w-7 h-7 rounded-lg bg-[var(--primary)] flex items-center justify-center font-bold text-sm">{selectedIds.length}</span>
        <span className="text-sm">selected</span>
      </div>
      <button ref={statusRef} onClick={() => setSOpen(o => !o)} className="flex flex-col items-center gap-0.5 px-4 py-2 hover:bg-white/10 transition-colors"><Icon.bolt size={16} /><span className="text-[11px]">Status</span></button>
      <button ref={prioRef} onClick={() => setPOpen(o => !o)} className="flex flex-col items-center gap-0.5 px-4 py-2 hover:bg-white/10 transition-colors"><Icon.arrowUp size={16} /><span className="text-[11px]">Priority</span></button>
      <button onClick={() => deleteTasks(selectedIds)} className="flex flex-col items-center gap-0.5 px-4 py-2 hover:bg-[#e2445c]/30 transition-colors text-[#ff8a9b]"><Icon.trash size={16} /><span className="text-[11px]">Delete</span></button>
      <button onClick={clearSelection} className="flex items-center justify-center px-3 py-2 hover:bg-white/10 border-l border-white/10 h-full"><Icon.x size={18} /></button>

      <Popover anchorRef={statusRef} open={sOpen} onClose={() => setSOpen(false)} width={180} className="p-2 flex flex-col gap-1">
        {STATUS_ORDER.map(s => <button key={s} onClick={() => { selectedIds.forEach(id => setStatus(id, s)); setSOpen(false); }} className="rounded px-2 py-1.5 text-[13px] font-semibold text-left" style={{ background: STATUS_CONFIG[s].hex, color: textOn(STATUS_CONFIG[s].hex) }}>{STATUS_CONFIG[s].label}</button>)}
      </Popover>
      <Popover anchorRef={prioRef} open={pOpen} onClose={() => setPOpen(false)} width={160} className="p-2 flex flex-col gap-1">
        {PRIORITY_ORDER.map(p => <button key={p} onClick={() => { selectedIds.forEach(id => updateTask(id, { priority: p })); setPOpen(false); }} className="rounded px-2 py-1.5 text-[13px] font-semibold text-left" style={{ background: PRIORITY_CONFIG[p].hex, color: textOn(PRIORITY_CONFIG[p].hex) }}>{PRIORITY_CONFIG[p].label}</button>)}
      </Popover>
    </div>
  );
}
