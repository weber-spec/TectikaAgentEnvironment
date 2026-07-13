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
  const { runTask, stopTask, resetAndRun, isTaskRunning, unmetUpstreamIds } = useBoard();
  const [hovered, setHovered] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);

  if (task.assignee?.type !== 'Agent') return null;

  const running = isTaskRunning(task);
  const needsReset = !running && task.status !== 'Backlog';   // Done/Failed/Review/paused → Reset
  // Only parents that aren't Done. A task whose dependencies are all satisfied gets the plain
  // tooltip — the warning used to fire on any upstream edge, satisfied or not.
  const hasUnmetDep = (unmetUpstreamIds[task.id]?.length ?? 0) > 0;
  const title = running
    ? 'Stop this run'
    : needsReset
      ? "Reset this task and start a fresh run (clears the agent's memory)"
      : hasUnmetDep
        ? "Run this task's agent (upstream tasks aren't done yet)"
        : "Run this task's agent";

  const onClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (running) setConfirmOpen(true);
    else if (needsReset) setResetOpen(true);
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

  const resetModal = (
    <Modal open={resetOpen} onClose={() => setResetOpen(false)} title="Reset this task?" width={440} z={1300}
      footer={<>
        <Button variant="secondary" size="sm" onClick={() => setResetOpen(false)}>Cancel</Button>
        <Button variant="primary" size="sm" onClick={() => { resetAndRun(task.id); setResetOpen(false); }}>Reset &amp; run</Button>
      </>}>
      <p className="text-sm text-[var(--muted)]">
        This discards the current result and <span className="font-semibold text-[var(--foreground)]">clears the agent&apos;s
        memory</span>, then starts a fresh run — the agent won&apos;t remember the previous attempt. To continue the
        existing work instead, message the agent in the <span className="font-semibold text-[var(--foreground)]">Chat</span> tab.
      </p>
    </Modal>
  );

  const idleColor = needsReset ? 'bg-[#fdab3d] hover:bg-[#f59e0b]' : 'bg-green-600 hover:bg-green-700';
  const idleLabel = needsReset
    ? <><Icon.refresh aria-hidden="true" size={14} /> Reset</>
    : <><Icon.play aria-hidden="true" size={14} /> Run</>;

  if (mode === 'icon') {
    return (
      <>
        <button {...hoverProps} onClick={onClick} title={title}
          aria-label={running ? 'Stop this run' : needsReset ? 'Reset this task' : 'Run this task'}
          className={`inline-flex items-center justify-center w-6 h-6 rounded-md shrink-0 transition-all ${
            running
              ? `${showStop ? 'text-[#e2445c]' : 'text-green-600'} hover:bg-[var(--surface)]`
              : `${needsReset ? 'text-[#fdab3d] hover:text-[#f59e0b]' : 'text-[var(--muted)] hover:text-green-600'} opacity-0 group-hover:opacity-100 hover:bg-[var(--surface)]`
          }`}
        >
          {running ? (showStop ? StopGlyph : Spinner) : needsReset ? <Icon.refresh size={14} /> : <Icon.play size={14} />}
        </button>
        {confirmModal}{resetModal}
      </>
    );
  }

  return (
    <>
      <button {...hoverProps} onClick={onClick} title={title}
        className={`inline-flex items-center gap-1.5 text-xs font-semibold rounded-md whitespace-nowrap px-2.5 py-1.5 text-white shadow-sm transition-all ${
          running ? (showStop ? 'bg-[#e2445c]' : 'bg-green-600') : idleColor
        }`}
      >
        {running
          ? (showStop ? <>{StopGlyph} Stop</> : <>{Spinner} Running</>)
          : idleLabel}
      </button>
      {confirmModal}{resetModal}
    </>
  );
}
