'use client';

import { Handle, Position, type NodeProps } from '@xyflow/react';
import { StatusBadge } from '../board/StatusBadge';
import type { AgentTask, AgentRole } from '@/lib/types';

interface AgentNodeData {
  task: AgentTask;
  role?: AgentRole;
  onOpen: (taskId: string) => void;
}

export function AgentNode({ data }: NodeProps) {
  const { task, role, onOpen } = data as unknown as AgentNodeData;

  return (
    <div
      className="bg-white border-2 border-gray-200 rounded-xl shadow-sm w-56 cursor-pointer hover:border-blue-400 transition-colors"
      onDoubleClick={() => onOpen(task.id)}
    >
      {/* Input port */}
      <Handle type="target" position={Position.Left} className="!w-3 !h-3 !bg-blue-400" />

      <div className="p-3">
        <div className="flex items-center justify-between mb-2">
          <span className="text-xs text-gray-400">🤖 {role?.displayName ?? task.assignee.id}</span>
          <StatusBadge status={task.status} />
        </div>
        <p className="font-medium text-sm leading-tight">{task.title}</p>
      </div>

      {/* Output port */}
      <Handle type="source" position={Position.Right} className="!w-3 !h-3 !bg-green-400" />
    </div>
  );
}
