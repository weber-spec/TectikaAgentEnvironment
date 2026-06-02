'use client';

import Link from 'next/link';
import { StatusBadge } from './StatusBadge';
import type { AgentTask, AgentRole } from '@/lib/types';

const PRIORITY_CLASS: Record<string, string> = {
  Critical: 'text-red-400 bg-red-500/10 border-red-500/30',
  High:     'text-orange-400 bg-orange-500/10 border-orange-500/30',
  Medium:   'text-amber-400 bg-amber-500/10 border-amber-500/30',
  Low:      'text-[#8892aa] bg-[#232a3b] border-[#2d3651]',
};

interface Props {
  task: AgentTask;
  roles: AgentRole[];
  onStatusChange?: (taskId: string, status: AgentTask['status']) => void;
}

export function TaskRow({ task, roles }: Props) {
  const role = roles.find(r => r.id === task.assignee.id);

  return (
    <tr className="border-b border-[#2d3651] hover:bg-[#1a1f2e] transition-colors group">
      {/* Title */}
      <td className="px-4 py-3">
        <Link
          href={`/workspace/${task.boardId}/${task.id}`}
          className="font-medium text-sm text-[#e8ecf4] hover:text-indigo-300 transition-colors"
        >
          {task.title}
        </Link>
      </td>

      {/* Assignee */}
      <td className="px-4 py-3">
        <div className="flex items-center gap-2">
          <span className="text-base">
            {task.assignee.type === 'Agent' ? '🤖' : '👤'}
          </span>
          <span className="text-sm text-[#8892aa]">
            {task.assignee.type === 'Agent'
              ? (role?.displayName ?? task.assignee.id)
              : task.assignee.id}
          </span>
        </div>
      </td>

      {/* Status */}
      <td className="px-4 py-3">
        <StatusBadge status={task.status} />
      </td>

      {/* Upstream */}
      <td className="px-4 py-3 text-xs text-[#8892aa]">
        {task.upstreamTaskIds.length > 0
          ? <span className="text-cyan-400">← {task.upstreamTaskIds.length}</span>
          : '—'}
      </td>

      {/* Downstream */}
      <td className="px-4 py-3 text-xs text-[#8892aa]">
        {task.downstreamTaskIds.length > 0
          ? <span className="text-indigo-400">→ {task.downstreamTaskIds.length}</span>
          : '—'}
      </td>

      {/* Priority */}
      <td className="px-4 py-3">
        <span className={`text-xs px-2 py-0.5 rounded border ${PRIORITY_CLASS[task.priority]}`}>
          {task.priority}
        </span>
      </td>
    </tr>
  );
}
