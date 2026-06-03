'use client';

import Link from 'next/link';
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

export function TaskRow({ task, roles }: Props) {
  const role = roles.find(r => r.id === task.assignee.id);
  const assigneeName = task.assignee.type === 'Agent'
    ? (role?.displayName ?? task.assignee.id)
    : task.assignee.id;
  const initials = assigneeName.slice(0, 2).toUpperCase();

  return (
    <tr className="monday-row border-b border-[#e6e9ef] transition-colors" style={{ height: '40px' }}>
      {/* Checkbox */}
      <td className="pl-3 pr-1 w-8">
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
          className="text-sm text-[#323338] font-medium hover:text-[#0073ea] transition-colors"
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
        <StatusBadge status={task.status} />
      </td>

      {/* Priority */}
      <td className="px-3 py-2 w-28">
        <div className="flex items-center gap-1.5">
          <span
            className="w-2 h-2 rounded-full shrink-0"
            style={{ background: PRIORITY_DOT[task.priority] ?? '#c4c4c4' }}
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
