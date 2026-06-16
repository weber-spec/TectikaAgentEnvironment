'use client';

import React, { useState } from 'react';
import {
  DndContext, PointerSensor, useSensor, useSensors, useDraggable, useDroppable,
  DragOverlay, type DragEndEvent, type DragStartEvent,
} from '@dnd-kit/core';
import { useBoard } from '@/lib/board-context';
import type { AgentTask, AgentTaskStatus, TaskPriority } from '@/lib/types';
import type { TaskGroup } from '@/lib/board-engine';
import { Avatar, Pill } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { RunTaskButton } from '@/components/board/RunTaskButton';
import { STATUS_CONFIG, PRIORITY_CONFIG, alpha } from '@/lib/palette';
import { formatDateShort, daysUntil } from '@/lib/format';

export function KanbanView() {
  const { groups, activeView, updateTask, setStatus, addTask, openTask, peopleById } = useBoard();
  const [dragId, setDragId] = useState<string | null>(null);
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));
  const groupBy = activeView.groupBy ?? 'status';

  const onStart = (e: DragStartEvent) => setDragId(String(e.active.id));
  const onEnd = (e: DragEndEvent) => {
    setDragId(null);
    const taskId = String(e.active.id);
    const targetKey = e.over ? String(e.over.id) : null;
    if (!targetKey) return;
    if (groupBy === 'status') setStatus(taskId, targetKey as AgentTaskStatus);
    else if (groupBy === 'priority') updateTask(taskId, { priority: targetKey as TaskPriority });
    else if (groupBy === 'people') updateTask(taskId, { assignee: { type: peopleById[targetKey]?.kind ?? 'Human', id: targetKey } });
  };

  const allTasks = groups.flatMap(g => g.tasks);
  const dragged = allTasks.find(t => t.id === dragId);

  return (
    <DndContext sensors={sensors} onDragStart={onStart} onDragEnd={onEnd}>
      <div className="h-full overflow-x-auto overflow-y-hidden p-4">
        <div className="flex gap-3 h-full items-start">
          {groups.map(g => (
            <Column key={g.key} group={g} groupBy={groupBy} onAdd={(title) => addTask({ title }, g.key !== '__all__' ? { colId: groupBy, key: g.key } : undefined)} onOpen={openTask} />
          ))}
        </div>
      </div>
      <DragOverlay>{dragged && <Card task={dragged} dragging onOpen={() => {}} />}</DragOverlay>
    </DndContext>
  );
}

function Column({ group, groupBy, onAdd, onOpen }: { group: TaskGroup; groupBy: string; onAdd: (t: string) => void; onOpen: (id: string) => void }) {
  void groupBy;
  const { setNodeRef, isOver } = useDroppable({ id: group.key });
  const [adding, setAdding] = useState(false);
  const [val, setVal] = useState('');
  return (
    <div className="w-[300px] shrink-0 flex flex-col max-h-full rounded-xl" style={{ background: alpha(group.hex, 0.06) }}>
      <div className="flex items-center gap-2 px-3 py-2.5 sticky top-0">
        <span className="w-2.5 h-2.5 rounded-full" style={{ background: group.hex }} />
        <span className="font-semibold text-sm" style={{ color: group.hex }}>{group.label}</span>
        <span className="text-xs text-[var(--muted)] bg-[var(--background)] rounded-full px-1.5">{group.tasks.length}</span>
      </div>
      <div ref={setNodeRef} className={`flex-1 overflow-y-auto px-2 pb-2 flex flex-col gap-2 transition-colors rounded-lg ${isOver ? 'bg-[var(--primary-light)]' : ''}`} style={{ minHeight: 80 }}>
        {group.tasks.map(t => <DraggableCard key={t.id} task={t} onOpen={onOpen} />)}
        {adding ? (
          <input autoFocus value={val} onChange={e => setVal(e.target.value)}
            onBlur={() => { if (val.trim()) onAdd(val.trim()); setVal(''); setAdding(false); }}
            onKeyDown={e => { if (e.key === 'Enter') { if (val.trim()) onAdd(val.trim()); setVal(''); setAdding(false); } if (e.key === 'Escape') { setAdding(false); setVal(''); } }}
            placeholder="Item title…" className="rounded-lg border border-[var(--primary)] bg-[var(--background)] px-3 py-2 text-sm outline-none text-[var(--foreground)]" />
        ) : (
          <button onClick={() => setAdding(true)} className="flex items-center gap-1.5 px-2 py-1.5 text-sm text-[var(--muted)] hover:text-[var(--primary)] rounded-lg hover:bg-[var(--background)]"><Icon.plus size={14} /> Add item</button>
        )}
      </div>
    </div>
  );
}

function DraggableCard({ task, onOpen }: { task: AgentTask; onOpen: (id: string) => void }) {
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({ id: task.id });
  const style = transform ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)`, opacity: isDragging ? 0.3 : 1 } : { opacity: isDragging ? 0.3 : 1 };
  return <div ref={setNodeRef} style={style} {...attributes} {...listeners}><Card task={task} onOpen={onOpen} /></div>;
}

function Card({ task, onOpen, dragging }: { task: AgentTask; onOpen: (id: string) => void; dragging?: boolean }) {
  const { peopleById, upstreamIds, downstreamIds } = useBoard();
  const person = peopleById[task.assignee.id];
  const linkCount = (upstreamIds[task.id]?.length ?? 0) + (downstreamIds[task.id]?.length ?? 0);
  const dd = daysUntil(task.dueAt);
  const overdue = dd != null && dd < 0 && task.status !== 'Done';
  return (
    <div onClick={() => onOpen(task.id)}
      className={`group bg-[var(--background)] rounded-lg border border-[var(--border)] p-3 cursor-pointer hover:shadow-md transition-shadow ${dragging ? 'shadow-2xl rotate-2' : ''}`}
      style={{ borderLeft: `3px solid ${STATUS_CONFIG[task.status].hex}` }}>
      <div className="text-sm font-medium text-[var(--foreground)] mb-2 line-clamp-2">{task.title}</div>
      <div className="flex items-center gap-1.5 mb-2 flex-wrap">
        <Pill label={PRIORITY_CONFIG[task.priority].label} hex={PRIORITY_CONFIG[task.priority].hex} />
        {task.dueAt && <span className={`text-[11px] inline-flex items-center gap-1 ${overdue ? 'text-[#e2445c]' : 'text-[var(--muted)]'}`}><Icon.calendar size={11} />{formatDateShort(task.dueAt)}</span>}
      </div>
      <div className="flex items-center justify-between">
        <Avatar person={person} name={task.assignee.id} size={22} />
        <div className="flex items-center gap-1.5">
          {linkCount > 0 && (
            <span className="text-[11px] text-[var(--primary)] inline-flex items-center gap-0.5"><Icon.link size={11} />{linkCount}</span>
          )}
          <RunTaskButton task={task} mode="icon" />
        </div>
      </div>
    </div>
  );
}
