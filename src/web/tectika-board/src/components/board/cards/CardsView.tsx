'use client';

import React from 'react';
import { useBoard } from '@/lib/board-context';
import { Avatar, Pill } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { STATUS_CONFIG, PRIORITY_CONFIG } from '@/lib/palette';
import { formatDateShort, daysUntil, relativeTime } from '@/lib/format';

export function CardsView() {
  const { visibleTasks, peopleById, openTask } = useBoard();
  return (
    <div className="h-full overflow-auto p-4">
      <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' }}>
        {visibleTasks.map(task => {
          const person = peopleById[task.assignee.id];
          const dd = daysUntil(task.dueAt);
          const overdue = dd != null && dd < 0 && task.status !== 'Done';
          const st = STATUS_CONFIG[task.status];
          return (
            <button key={task.id} onClick={() => openTask(task.id)}
              className="text-left bg-[var(--background)] rounded-xl border border-[var(--border)] overflow-hidden hover:shadow-lg transition-shadow group">
              <div className="h-1.5" style={{ background: st.hex }} />
              <div className="p-3.5">
                <div className="flex items-start justify-between gap-2 mb-2">
                  <span className="text-sm font-semibold text-[var(--foreground)] line-clamp-2">{task.title}</span>
                  <Avatar person={person} name={task.assignee.id} size={26} />
                </div>
                {task.description && <p className="text-xs text-[var(--muted)] line-clamp-2 mb-3">{task.description}</p>}
                <div className="flex items-center gap-1.5 flex-wrap mb-2">
                  <Pill label={st.label} hex={st.hex} />
                  <Pill label={PRIORITY_CONFIG[task.priority].label} hex={PRIORITY_CONFIG[task.priority].hex} />
                </div>
                <div className="flex items-center justify-between text-[11px] text-[var(--muted)]">
                  <span className="inline-flex items-center gap-1">{task.dueAt ? <><Icon.calendar size={12} /><span className={overdue ? 'text-[#e2445c]' : ''}>{formatDateShort(task.dueAt)}</span></> : <span className="text-[var(--muted-2)]">No date</span>}</span>
                  <span>{relativeTime(task.createdAt)}</span>
                </div>
              </div>
            </button>
          );
        })}
      </div>
    </div>
  );
}
