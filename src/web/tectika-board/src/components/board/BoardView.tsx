'use client';

import React, { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { useBoard } from '@/lib/board-context';
import { CURRENT_USER } from '@/lib/collaboration';
import type { Board } from '@/lib/types';
import { Toolbar } from './Toolbar';
import { ViewTabs } from './ViewTabs';
import { BatchToolbar } from './BatchToolbar';
import { TableView } from './table/TableView';
import { KanbanView } from './kanban/KanbanView';
import { CalendarView } from './calendar/CalendarView';
import { TimelineView } from './timeline/TimelineView';
import { CardsView } from './cards/CardsView';
import { ChartView } from './chart/ChartView';
import { CanvasView } from './canvas/CanvasView';
import { RepoView } from '@/components/board/repo/RepoView';
import { ItemPanel } from '@/components/workspace/ItemPanel';
import { AutomationsModal } from '@/components/automations/AutomationsModal';
import { GitHubConnectModal } from '@/components/board/GitHubConnectModal';
import { BoardSettingsModal } from '@/components/board/settings/BoardSettingsModal';
import { SkeletonRows, Button, EmptyState } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';

export function BoardView() {
  const { loading, error, board, activeView, automations, tasks, addTask, repoChangesTarget, clearRepoChangesTarget, repoFileTarget, clearRepoFileTarget } = useBoard();
  const [autoOpen, setAutoOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [githubOpen, setGithubOpen] = useState(false);
  const [showRepo, setShowRepo] = useState(false);
  const settingsRef = useRef<HTMLButtonElement>(null);

  // Local overrides so header updates instantly after save
  const [nameOverride, setNameOverride] = useState<string | null>(null);
  const [descOverride, setDescOverride] = useState<string | null>(null);
  const [boardOverride, setBoardOverride] = useState<Board | null>(null);

  const isOwner = board?.ownerId === CURRENT_USER.id;

  useEffect(() => {
    if (repoChangesTarget) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- open Repo tab when deep-link arrives
      setShowRepo(true);
      const t = setTimeout(() => clearRepoChangesTarget(), 0);
      return () => clearTimeout(t);
    }
  }, [repoChangesTarget, clearRepoChangesTarget]);

  useEffect(() => {
    if (repoFileTarget) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- open Repo/Files tab when a file deep-link arrives
      setShowRepo(true);
      const t = setTimeout(() => clearRepoFileTarget(), 0);
      return () => clearTimeout(t);
    }
  }, [repoFileTarget, clearRepoFileTarget]);

  const effectiveBoard = boardOverride ?? board;

  if (error) return <div className="flex flex-col items-center justify-center h-full text-[var(--muted)] gap-2"><Icon.warning size={32} /><p>{error}</p><p className="text-xs">Is the API running on {process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5000'}?</p></div>;

  return (
    <div className="flex flex-col h-full relative">
      {/* header */}
      <div className="px-5 pt-3 pb-2 border-b border-[var(--border)] flex items-center gap-3">
        <Link href="/boards" className="text-[var(--muted)] hover:text-[var(--foreground)]"><Icon.chevronLeft size={18} /></Link>
        <div className="min-w-0">
          <h1 className="text-lg font-bold text-[var(--foreground)] truncate leading-tight">{loading ? 'Loading…' : (nameOverride ?? board?.name ?? 'Board')}</h1>
          {(descOverride ?? board?.description) && <p className="text-xs text-[var(--muted)] truncate">{descOverride ?? board?.description}</p>}
        </div>
        <div className="flex-1" />
        <LiveIndicator />
        {effectiveBoard?.github && (
          <button onClick={() => setGithubOpen(true)} className="flex items-center gap-1.5 px-2 py-1 rounded-md text-[12px] text-emerald-600 dark:text-emerald-400 hover:bg-emerald-500/10" title={effectiveBoard.github.repoUrl}>
            <svg width={13} height={13} viewBox="0 0 98 96" fill="currentColor"><path fillRule="evenodd" clipRule="evenodd" d="M48.854 0C21.839 0 0 22 0 49.217c0 21.756 13.993 40.172 33.405 46.69 2.427.49 3.316-1.059 3.316-2.362 0-1.141-.08-5.052-.08-9.127-13.59 2.934-16.42-5.867-16.42-5.867-2.184-5.704-5.42-7.17-5.42-7.17-4.448-3.015.324-3.015.324-3.015 4.934.326 7.523 5.052 7.523 5.052 4.367 7.496 11.404 5.378 14.235 4.074.404-3.178 1.699-5.378 3.074-6.6-10.839-1.141-22.243-5.378-22.243-24.283 0-5.378 1.94-9.778 5.014-13.2-.485-1.222-2.184-6.275.486-13.038 0 0 4.125-1.304 13.426 5.052a46.97 46.97 0 0 1 12.214-1.63c4.125 0 8.33.571 12.213 1.63 9.302-6.356 13.427-5.052 13.427-5.052 2.67 6.763.97 11.816.485 13.038 3.155 3.422 5.015 7.822 5.015 13.2 0 18.905-11.404 23.06-22.324 24.283 1.78 1.548 3.316 4.481 3.316 9.126 0 6.6-.08 11.897-.08 13.526 0 1.304.89 2.853 3.316 2.364 19.412-6.52 33.405-24.935 33.405-46.691C97.707 22 75.788 0 48.854 0z" /></svg>
            {effectiveBoard.github.repo}
          </button>
        )}
        <button onClick={() => setAutoOpen(true)} className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-[13px] font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)] relative">
          <Icon.bolt size={16} /> Automate
          {automations.length > 0 && <span className="ml-0.5 text-[10px] bg-[var(--primary)] text-white rounded-full px-1.5">{automations.filter(a => a.enabled).length}</span>}
        </button>
        <button
          ref={settingsRef}
          onClick={() => setSettingsOpen(true)}
          className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-[13px] font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]"
          title="Board settings"
        >
          <Icon.settings size={15} />
        </button>
      </div>

      <ViewTabs repoActive={showRepo} onRepoClick={() => setShowRepo(true)} onViewSelect={() => setShowRepo(false)} />
      {!showRepo && <Toolbar />}

      <div className="flex-1 min-h-0 relative">
        {showRepo ? (
          board ? <RepoView boardId={board.id} onConnectGitHub={() => setGithubOpen(true)} changesTarget={repoChangesTarget} fileTarget={repoFileTarget} /> : null
        ) : loading ? <SkeletonRows rows={8} /> : tasks.length === 0 ? (
          <EmptyState icon={<Icon.board size={48} />} title="This board is empty"
            description="Add your first item to start orchestrating agents and humans."
            action={<Button variant="primary" onClick={() => addTask({ title: 'New item' })}><Icon.plus size={16} /> Add item</Button>} />
        ) : (
          <ActiveView kind={activeView.kind} />
        )}
        <BatchToolbar />
      </div>

      <ItemPanel />
      <AutomationsModal open={autoOpen} onClose={() => setAutoOpen(false)} />
      {githubOpen && effectiveBoard && (
        <GitHubConnectModal
          board={effectiveBoard}
          onClose={() => setGithubOpen(false)}
          onUpdated={updated => setBoardOverride(updated)}
        />
      )}

      {settingsOpen && effectiveBoard && (
        <BoardSettingsModal
          board={effectiveBoard}
          isOwner={isOwner}
          onClose={() => setSettingsOpen(false)}
          onBoardUpdated={updated => { setBoardOverride(updated); setNameOverride(updated.name); setDescOverride(updated.description); }}
        />
      )}
    </div>
  );
}

function LiveIndicator() {
  const { liveState, toggleLive } = useBoard();
  const cfg = {
    live:         { color: '#00c875', label: 'Live',           pulse: true },
    reconnecting: { color: '#fdab3d', label: 'Reconnecting…',  pulse: true },
    paused:       { color: '#c4c4c4', label: 'Paused',         pulse: false },
  }[liveState];
  return (
    <button onClick={toggleLive}
      title={liveState === 'paused' ? 'Live updates paused — click to resume' : 'Live updates on — click to pause'}
      className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-[12px] font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]">
      <span className="relative flex items-center justify-center w-2 h-2">
        {cfg.pulse && <span className="absolute inline-flex h-full w-full rounded-full opacity-60 animate-ping" style={{ background: cfg.color }} />}
        <span className="relative inline-flex rounded-full w-2 h-2" style={{ background: cfg.color }} />
      </span>
      {cfg.label}
    </button>
  );
}

function ActiveView({ kind }: { kind: string }) {
  switch (kind) {
    case 'table': return <TableView />;
    case 'kanban': return <KanbanView />;
    case 'calendar': return <CalendarView />;
    case 'timeline': return <TimelineView />;
    case 'cards': return <CardsView />;
    case 'chart': return <ChartView />;
    case 'canvas': return <CanvasView />;
    default: return <TableView />;
  }
}
