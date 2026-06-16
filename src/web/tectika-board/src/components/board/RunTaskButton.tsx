'use client';

import React from 'react';
import { useBoard } from '@/lib/board-context';
import type { AgentTask } from '@/lib/types';
import { Icon } from '@/components/ui/icons';

/**
 * Triggers the agent for a single task. Hidden for human-owned tasks; shows a
 * running state while the agent works; warns (but still allows) when upstream
 * dependencies aren't done.
 *
 * - mode="button": labelled pill, used in the item-panel header.
 * - mode="icon":   compact icon button, used on board cards (hover-revealed).
 */
export function RunTaskButton({ task, mode = 'button' }: { task: AgentTask; mode?: 'button' | 'icon' }) {
  const { runTask, isTaskRunning, upstreamIds } = useBoard();

  if (task.assignee?.type !== 'Agent') return null;

  const running = isTaskRunning(task);
  const hasUnmetDep = (upstreamIds[task.id]?.length ?? 0) > 0;
  const title = running
    ? 'Agent is running…'
    : hasUnmetDep
      ? "Run this task's agent (upstream tasks aren't done yet)"
      : "Run this task's agent";

  const onClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!running) runTask(task.id);
  };

  const Spinner = (
    <div className="border-2 border-current border-t-transparent rounded-full animate-spin flex-shrink-0" style={{ width: 12, height: 12 }} />
  );

  if (mode === 'icon') {
    return (
      <button
        onClick={onClick}
        disabled={running}
        title={title}
        aria-label={running ? 'Agent is running' : 'Run this task'}
        className={`inline-flex items-center justify-center w-6 h-6 rounded-md shrink-0 transition-all ${
          running
            ? 'text-green-600 cursor-default'
            : 'text-[var(--muted)] opacity-0 group-hover:opacity-100 hover:text-green-600 hover:bg-[var(--surface)]'
        }`}
      >
        {running ? Spinner : <Icon.play size={14} />}
      </button>
    );
  }

  return (
    <button
      onClick={onClick}
      disabled={running}
      title={title}
      className={`inline-flex items-center gap-1.5 text-xs font-semibold rounded-md whitespace-nowrap px-2.5 py-1.5 text-white shadow-sm transition-all ${
        running ? 'bg-green-600 opacity-70 cursor-not-allowed' : 'bg-green-600 hover:bg-green-700'
      }`}
    >
      {running ? Spinner : <Icon.play aria-hidden="true" size={14} />}
      {running ? 'Running' : 'Run'}
    </button>
  );
}
