'use client';

import React, { useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useBoard } from '@/lib/board-context';
import { CURRENT_USER } from '@/lib/collaboration';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
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
import { Menu, Modal } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';

export function BoardView() {
  const { loading, error, board, activeView, automations, tasks, addTask } = useBoard();
  const router = useRouter();
  const [autoOpen, setAutoOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [editName, setEditName] = useState('');
  const [editDesc, setEditDesc] = useState('');
  const [editSaving, setEditSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const settingsRef = useRef<HTMLButtonElement>(null);

  // Local overrides so header updates instantly after save
  const [nameOverride, setNameOverride] = useState<string | null>(null);
  const [descOverride, setDescOverride] = useState<string | null>(null);

  const isOwner = board?.ownerId === CURRENT_USER.id;

  const openEdit = () => {
    setEditName(nameOverride ?? board?.name ?? '');
    setEditDesc(descOverride ?? board?.description ?? '');
    setEditOpen(true);
  };

  const handleSaveEdit = async () => {
    if (!board || !editName.trim()) return;
    setEditSaving(true);
    try {
      await api.boards.update(board.id, editName.trim(), editDesc.trim() || undefined);
      setNameOverride(editName.trim());
      setDescOverride(editDesc.trim());
      setEditOpen(false);
      toast('Board updated', 'success');
    } catch { toast('Could not update board', 'error'); }
    finally { setEditSaving(false); }
  };

  const handleDelete = async () => {
    if (!board) return;
    setDeleting(true);
    try {
      await api.boards.remove(board.id);
      toast('Board deleted', 'success');
      router.push('/boards');
    } catch {
      toast('Could not delete board', 'error');
      setDeleting(false);
      setDeleteOpen(false);
    }
  };

  const settingsOptions = [
    { label: 'Rename board', icon: <Icon.edit size={14} />, onClick: openEdit },
    { label: 'Edit description', icon: <Icon.edit size={14} />, onClick: openEdit },
    'divider' as const,
    ...(isOwner ? [{ label: 'Delete board', danger: true, icon: <Icon.trash size={14} />, onClick: () => setDeleteOpen(true) }] : []),
  ];

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
        <button onClick={() => setAutoOpen(true)} className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-[13px] font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)] relative">
          <Icon.bolt size={16} /> Automate
          {automations.length > 0 && <span className="ml-0.5 text-[10px] bg-[var(--primary)] text-white rounded-full px-1.5">{automations.filter(a => a.enabled).length}</span>}
        </button>
        <button
          ref={settingsRef}
          onClick={() => setSettingsOpen(o => !o)}
          className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-[13px] font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]"
          title="Board settings"
        >
          <Icon.settings size={15} />
        </button>
        <Menu anchorRef={settingsRef} open={settingsOpen} onClose={() => setSettingsOpen(false)} width={200} options={settingsOptions} />
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

      {/* Edit board modal */}
      <Modal
        open={editOpen}
        onClose={() => setEditOpen(false)}
        title="Board settings"
        width={480}
        footer={
          <>
            <Button variant="ghost" onClick={() => setEditOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleSaveEdit} disabled={!editName.trim() || editSaving}>
              {editSaving ? 'Saving…' : 'Save'}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-[var(--muted)]">Board name <span className="text-[#e2445c]">*</span></label>
            <input
              autoFocus
              value={editName}
              onChange={e => setEditName(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') handleSaveEdit(); }}
              className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)] transition-colors"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-[var(--muted)]">Description</label>
            <textarea
              value={editDesc}
              onChange={e => setEditDesc(e.target.value)}
              rows={3}
              className="w-full rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)] transition-colors resize-none"
            />
          </div>
        </div>
      </Modal>

      {/* Delete confirm modal */}
      <Modal
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        title="Delete board"
        width={400}
        footer={
          <>
            <Button variant="ghost" onClick={() => setDeleteOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleDelete} disabled={deleting}
              style={{ background: '#e2445c', borderColor: '#e2445c' }}>
              {deleting ? 'Deleting…' : 'Delete board'}
            </Button>
          </>
        }
      >
        <p className="text-sm text-[var(--foreground)]">
          This will permanently delete <strong>{nameOverride ?? board?.name}</strong> and all its tasks. This cannot be undone.
        </p>
      </Modal>
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
