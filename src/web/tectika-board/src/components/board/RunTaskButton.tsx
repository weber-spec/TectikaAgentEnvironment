'use client';

import React, { useState } from 'react';
import { useBoard } from '@/lib/board-context';
import type { AgentTask } from '@/lib/types';
import { Icon } from '@/components/ui/icons';
import { Button } from '@/components/ui/primitives';
import { Modal } from '@/components/ui/overlays';

/**
 * Per-task agent control. Hidden for human-owned tasks.
 *  - idle:    "Run" — starts the agent (warns, but still allows, on unmet deps).
 *  - running: shows the run state, and morphs into a red "Stop" on hover; clicking
 *             opens a confirm modal that cancels the run.
 *
 * mode="button": labelled pill (item-panel header). mode="icon": compact icon (board cards).
 */
export function RunTaskButton({ task, mode = 'button' }: { task: AgentTask; mode?: 'button' | 'icon' }) {
  const { runTask, stopTask, isTaskRunning, upstreamIds } = useBoard();
  const [hovered, setHovered] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);

  if (task.assignee?.type !== 'Agent') return null;

  const running = isTaskRunning(task);
  const hasUnmetDep = (upstreamIds[task.id]?.length ?? 0) > 0;
  const title = running
    ? 'Stop this run'
    : hasUnmetDep
      ? "Run this task's agent (upstream tasks aren't done yet)"
      : "Run this task's agent";

  const onClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (running) setConfirmOpen(true);
    else runTask(task.id);
  };

  const hoverProps = { onMouseEnter: () => setHovered(true), onMouseLeave: () => setHovered(false) };
  const showStop = running && hovered;

  const Spinner = (
    <div className="border-2 border-current border-t-transparent rounded-full animate-spin flex-shrink-0" style={{ width: 12, height: 12 }} />
  );
  const StopGlyph = <span className="rounded-[2px] bg-current flex-shrink-0" style={{ width: 9, height: 9 }} />;

  const confirmModal = (
    <Modal open={confirmOpen} onClose={() => setConfirmOpen(false)} title="Stop this run?" width={420} z={1300}
      footer={<>
        <Button variant="secondary" size="sm" onClick={() => setConfirmOpen(false)}>Cancel</Button>
        <Button variant="danger" size="sm" onClick={() => { stopTask(task.id); setConfirmOpen(false); }}>Stop run</Button>
      </>}>
      <p className="text-sm text-[var(--muted)]">The agent will cancel its current work.</p>
    </Modal>
  );

  if (mode === 'icon') {
    return (
      <>
        <button {...hoverProps} onClick={onClick} title={title}
          aria-label={running ? 'Stop this run' : 'Run this task'}
          className={`inline-flex items-center justify-center w-6 h-6 rounded-md shrink-0 transition-all ${
            running
              ? `${showStop ? 'text-[#e2445c]' : 'text-green-600'} hover:bg-[var(--surface)]`
              : 'text-[var(--muted)] opacity-0 group-hover:opacity-100 hover:text-green-600 hover:bg-[var(--surface)]'
          }`}
        >
          {running ? (showStop ? StopGlyph : Spinner) : <Icon.play size={14} />}
        </button>
        {confirmModal}
      </>
    );
  }

  return (
    <>
      <button {...hoverProps} onClick={onClick} title={title}
        className={`inline-flex items-center gap-1.5 text-xs font-semibold rounded-md whitespace-nowrap px-2.5 py-1.5 text-white shadow-sm transition-all ${
          running ? (showStop ? 'bg-[#e2445c]' : 'bg-green-600') : 'bg-green-600 hover:bg-green-700'
        }`}
      >
        {running
          ? (showStop ? <>{StopGlyph} Stop</> : <>{Spinner} Running</>)
          : <><Icon.play aria-hidden="true" size={14} /> Run</>}
      </button>
      {confirmModal}
    </>
  );
}
