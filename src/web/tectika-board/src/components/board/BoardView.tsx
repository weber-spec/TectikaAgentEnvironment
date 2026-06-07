'use client';

import React, { useState } from 'react';
import Link from 'next/link';
import { useBoard } from '@/lib/board-context';
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
import { ItemPanel } from '@/components/workspace/ItemPanel';
import { AutomationsModal } from '@/components/automations/AutomationsModal';
import { SkeletonRows, Button, EmptyState } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';

export function BoardView() {
  const { loading, error, board, activeView, automations, tasks, addTask } = useBoard();
  const [autoOpen, setAutoOpen] = useState(false);

  if (error) return <div className="flex flex-col items-center justify-center h-full text-[var(--muted)] gap-2"><Icon.warning size={32} /><p>{error}</p><p className="text-xs">Is the API running on {process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5000'}?</p></div>;

  return (
    <div className="flex flex-col h-full relative">
      {/* header */}
      <div className="px-5 pt-3 pb-2 border-b border-[var(--border)] flex items-center gap-3">
        <Link href="/boards" className="text-[var(--muted)] hover:text-[var(--foreground)]"><Icon.chevronLeft size={18} /></Link>
        <div className="min-w-0">
          <h1 className="text-lg font-bold text-[var(--foreground)] truncate leading-tight">{loading ? 'Loading…' : board?.name ?? 'Board'}</h1>
          {board?.description && <p className="text-xs text-[var(--muted)] truncate">{board.description}</p>}
        </div>
        <div className="flex-1" />
        <LiveIndicator />
        <button onClick={() => setAutoOpen(true)} className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-[13px] font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)] relative">
          <Icon.bolt size={16} /> Automate
          {automations.length > 0 && <span className="ml-0.5 text-[10px] bg-[var(--primary)] text-white rounded-full px-1.5">{automations.filter(a => a.enabled).length}</span>}
        </button>
      </div>

      <ViewTabs />
      <Toolbar />

      <div className="flex-1 min-h-0 relative">
        {loading ? <SkeletonRows rows={8} /> : tasks.length === 0 ? (
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
