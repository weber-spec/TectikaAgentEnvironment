'use client';

import Link from 'next/link';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { StatusBadge } from './StatusBadge';
import type { AgentTask, AgentRole } from '@/lib/types';

const PRIORITY_DOT: Record<string, string> = {
  Critical: '#e2445c',
  High:     '#ff642e',
  Medium:   '#fdab3d',
  Low:      '#c4c4c4',
};

interface Props {
  task: AgentTask;
  roles: AgentRole[];
  onStatusChange?: (taskId: string, status: AgentTask['status']) => void;
}

export function TaskRow({ task, roles, onStatusChange }: Props) {
  const role = roles.find(r => r.id === task.assignee.id);
  const assigneeName = task.assignee.type === 'Agent'
    ? (role?.displayName ?? task.assignee.id)
    : task.assignee.id;
  const initials = assigneeName.slice(0, 2).toUpperCase();

  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({ id: task.id });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    height: '40px',
    opacity: isDragging ? 0.4 : 1,
    boxShadow: isDragging ? '0 8px 24px rgba(0,0,0,0.15)' : undefined,
    zIndex: isDragging ? 50 : undefined,
    background: isDragging ? '#f0f4ff' : undefined,
  };

  return (
    <tr
      ref={setNodeRef}
      style={style}
      className="monday-row border-b border-[#e6e9ef] transition-all group/row"
    >
      {/* Drag handle */}
      <td className="pl-2 pr-0 w-6">
        <button
          {...attributes}
          {...listeners}
          className="w-5 h-8 flex items-center justify-center text-[#c3c6d4] opacity-0 group-hover/row:opacity-100 transition-opacity cursor-grab active:cursor-grabbing hover:text-[#676879]"
          tabIndex={-1}
          aria-label="Drag to reorder"
        >
          <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
            <circle cx="5" cy="4" r="1.5"/>
            <circle cx="11" cy="4" r="1.5"/>
            <circle cx="5" cy="8" r="1.5"/>
            <circle cx="11" cy="8" r="1.5"/>
            <circle cx="5" cy="12" r="1.5"/>
            <circle cx="11" cy="12" r="1.5"/>
          </svg>
        </button>
      </td>

      {/* Checkbox */}
      <td className="pl-1 pr-1 w-8">
        <input
          type="checkbox"
          className="rounded border-[#c3c6d4] accent-[#0073ea] cursor-pointer"
          readOnly
        />
      </td>

      {/* Title */}
      <td className="px-3 py-2 min-w-[220px]">
        <Link
          href={`/workspace/${task.boardId}/${task.id}`}
          className="text-sm text-[#323338] font-medium hover:text-[#0073ea] transition-colors group-hover/row:translate-x-0.5 inline-block transition-transform"
        >
          {task.title}
        </Link>
      </td>

      {/* Assignee */}
      <td className="px-3 py-2 w-36">
        <div className="flex items-center gap-2">
          <div
            className="w-6 h-6 rounded-full flex items-center justify-center text-white text-[10px] font-bold shrink-0"
            style={{ background: task.assignee.type === 'Agent' ? '#0073ea' : '#676879' }}
            title={assigneeName}
          >
            {task.assignee.type === 'Agent' ? '⚡' : initials}
          </div>
          <span className="text-xs text-[#676879] truncate max-w-[90px]">{assigneeName}</span>
        </div>
      </td>

      {/* Status */}
      <td className="px-3 py-2 w-36">
        <StatusBadge status={task.status} onStatusChange={onStatusChange ? (s) => onStatusChange(task.id, s) : undefined} />
      </td>

      {/* Priority */}
      <td className="px-3 py-2 w-28">
        <div className="flex items-center gap-1.5">
          <span
            className="w-2 h-2 rounded-full shrink-0"
            style={{
              background: PRIORITY_DOT[task.priority] ?? '#c4c4c4',
              animation: task.priority === 'Critical' ? 'pulse 1.5s ease-in-out infinite' : undefined,
            }}
          />
          <span className="text-xs text-[#676879]">{task.priority}</span>
        </div>
      </td>

      {/* Dependencies */}
      <td className="px-3 py-2 w-28 text-xs text-[#676879]">
        {task.upstreamTaskIds.length > 0 || task.downstreamTaskIds.length > 0 ? (
          <span className="text-[#0073ea] font-medium">
            {task.upstreamTaskIds.length > 0 && `↑${task.upstreamTaskIds.length}`}
            {task.upstreamTaskIds.length > 0 && task.downstreamTaskIds.length > 0 && ' '}
            {task.downstreamTaskIds.length > 0 && `↓${task.downstreamTaskIds.length}`}
          </span>
        ) : '—'}
      </td>
    </tr>
  );
}
