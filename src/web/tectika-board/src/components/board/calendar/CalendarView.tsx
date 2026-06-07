'use client';

import React, { useState } from 'react';
import { useBoard } from '@/lib/board-context';
import { IconButton } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { STATUS_CONFIG } from '@/lib/palette';

const DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

export function CalendarView() {
  const { visibleTasks, openTask, addTask } = useBoard();
  const [cursor, setCursor] = useState(() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1); });

  const year = cursor.getFullYear(); const month = cursor.getMonth();
  const firstDow = new Date(year, month, 1).getDay();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const today = new Date(); today.setHours(0, 0, 0, 0);

  const byDay = new Map<string, typeof visibleTasks>();
  for (const t of visibleTasks) {
    if (!t.dueAt) continue;
    const d = new Date(t.dueAt); const key = `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
    if (!byDay.has(key)) byDay.set(key, []);
    byDay.get(key)!.push(t);
  }

  const cells: ({ day: number } | null)[] = [];
  for (let i = 0; i < firstDow; i++) cells.push(null);
  for (let d = 1; d <= daysInMonth; d++) cells.push({ day: d });
  while (cells.length % 7 !== 0) cells.push(null);

  const unscheduled = visibleTasks.filter(t => !t.dueAt);

  return (
    <div className="h-full flex">
      <div className="flex-1 flex flex-col p-4 overflow-hidden">
        <div className="flex items-center gap-3 mb-3">
          <h2 className="text-lg font-semibold text-[var(--foreground)]">{cursor.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })}</h2>
          <div className="flex items-center gap-1">
            <IconButton label="Previous" onClick={() => setCursor(new Date(year, month - 1, 1))}><Icon.chevronLeft size={16} /></IconButton>
            <button onClick={() => { const d = new Date(); setCursor(new Date(d.getFullYear(), d.getMonth(), 1)); }} className="text-xs px-2 py-1 rounded border border-[var(--border)] text-[var(--muted)] hover:bg-[var(--surface)]">Today</button>
            <IconButton label="Next" onClick={() => setCursor(new Date(year, month + 1, 1))}><Icon.chevronRight size={16} /></IconButton>
          </div>
        </div>
        <div className="grid grid-cols-7 gap-px mb-px">
          {DOW.map(d => <div key={d} className="text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)] px-2 py-1">{d}</div>)}
        </div>
        <div className="grid grid-cols-7 gap-px flex-1 bg-[var(--border)] rounded-lg overflow-hidden" style={{ gridAutoRows: '1fr' }}>
          {cells.map((c, i) => {
            if (!c) return <div key={i} className="bg-[var(--surface)]/40" />;
            const date = new Date(year, month, c.day);
            const key = `${year}-${month}-${c.day}`;
            const items = byDay.get(key) ?? [];
            const isToday = date.getTime() === today.getTime();
            return (
              <div key={i} className="bg-[var(--background)] p-1 flex flex-col gap-0.5 overflow-hidden group/day min-h-[84px]">
                <div className="flex items-center justify-between">
                  <span className={`text-xs w-6 h-6 flex items-center justify-center rounded-full ${isToday ? 'bg-[var(--primary)] text-white font-bold' : 'text-[var(--muted)]'}`}>{c.day}</span>
                  <button onClick={() => addTask({ title: 'New item', dueAt: date.toISOString() })} className="opacity-0 group-hover/day:opacity-100 text-[var(--muted-2)] hover:text-[var(--primary)]"><Icon.plus size={13} /></button>
                </div>
                <div className="flex flex-col gap-0.5 overflow-hidden">
                  {items.slice(0, 3).map(t => (
                    <button key={t.id} onClick={() => openTask(t.id)} className="text-left text-[11px] px-1.5 py-0.5 rounded truncate text-white font-medium hover:opacity-90" style={{ background: STATUS_CONFIG[t.status].hex }}>{t.title}</button>
                  ))}
                  {items.length > 3 && <span className="text-[10px] text-[var(--muted)] px-1">+{items.length - 3} more</span>}
                </div>
              </div>
            );
          })}
        </div>
      </div>
      {unscheduled.length > 0 && (
        <div className="w-56 shrink-0 border-l border-[var(--border)] p-3 overflow-auto">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--muted)] mb-2">Unscheduled · {unscheduled.length}</h3>
          <div className="flex flex-col gap-1.5">
            {unscheduled.map(t => (
              <button key={t.id} onClick={() => openTask(t.id)} className="text-left text-xs p-2 rounded-lg border border-[var(--border)] hover:bg-[var(--surface)]" style={{ borderLeft: `3px solid ${STATUS_CONFIG[t.status].hex}` }}>{t.title}</button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
