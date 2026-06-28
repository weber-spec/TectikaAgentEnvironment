'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Board } from '@/lib/types';
import { GitHubConnectModal } from '@/components/board/GitHubConnectModal';
import { WorkspaceTab } from './WorkspaceTab';
import { ResetBoardDialog } from './ResetBoardDialog';
import { CloneBoardDialog } from './CloneBoardDialog';

type TabId = 'general' | 'repository' | 'workspace' | 'danger';

export function BoardSettingsModal({
  board, isOwner, onClose, onBoardUpdated,
}: {
  board: Board;
  isOwner: boolean;
  onClose: () => void;
  onBoardUpdated: (b: Board) => void;
}) {
  const router = useRouter();
  const [tab, setTab] = useState<TabId>('general');
  const [githubOpen, setGithubOpen] = useState(false);
  const [cloneOpen, setCloneOpen] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);

  const [name, setName] = useState(board.name);
  const [desc, setDesc] = useState(board.description);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const tabs: { id: TabId; label: string; icon: React.ReactNode; show: boolean }[] = [
    { id: 'general'    as const, label: 'General',     icon: <Icon.edit size={15} />,     show: true },
    { id: 'repository' as const, label: 'Repository',  icon: <Icon.flow size={15} />,     show: true },
    { id: 'workspace'  as const, label: 'Workspace',   icon: <Icon.terminal size={15} />, show: true },
    { id: 'danger'     as const, label: 'Danger Zone', icon: <Icon.warning size={15} />,  show: isOwner },
  ].filter(t => t.show);

  const saveGeneral = async () => {
    if (!name.trim()) return;
    setSaving(true);
    try {
      const updated = await api.boards.update(board.id, name.trim(), desc.trim() || undefined);
      onBoardUpdated(updated);
      toast('Board updated', 'success');
    } catch { toast('Could not update board', 'error'); }
    finally { setSaving(false); }
  };

  const handleDelete = async () => {
    setDeleting(true);
    try {
      await api.boards.remove(board.id);
      toast('Board deleted', 'success');
      router.push('/boards');
    } catch { toast('Could not delete board', 'error'); setDeleting(false); setDeleteOpen(false); }
  };

  return (
    <>
      <Modal open onClose={onClose} title="Board settings" width={720}>
        <div className="flex gap-5 min-h-[320px]">
          <nav className="w-44 shrink-0 flex flex-col gap-0.5 border-r border-[var(--border)] pr-2">
            {tabs.map(t => (
              <button
                key={t.id}
                onClick={() => setTab(t.id)}
                className={`flex items-center gap-2.5 px-3 py-2 rounded-md text-[13px] text-left transition-colors ${
                  tab === t.id ? 'bg-[var(--surface)] text-[var(--foreground)] font-medium'
                               : 'text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]'
                } ${t.id === 'danger' ? 'text-[#e2445c]' : ''}`}
              >
                <span className="shrink-0">{t.icon}</span>{t.label}
              </button>
            ))}
          </nav>

          <div className="flex-1 min-w-0">
            {tab === 'general' && (
              <div className="flex flex-col gap-4">
                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-[var(--muted)]">Board name <span className="text-[#e2445c]">*</span></label>
                  <input value={name} onChange={e => setName(e.target.value)}
                    className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)]" />
                </div>
                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-[var(--muted)]">Description</label>
                  <textarea value={desc} onChange={e => setDesc(e.target.value)} rows={3}
                    className="w-full rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)] resize-none" />
                </div>
                <div className="flex items-center justify-between pt-1">
                  <Button onClick={() => setCloneOpen(true)}><Icon.duplicate size={15} /> Clone board</Button>
                  <Button variant="primary" onClick={saveGeneral} disabled={!name.trim() || saving}>{saving ? 'Saving…' : 'Save'}</Button>
                </div>
              </div>
            )}

            {tab === 'repository' && (
              <div className="flex flex-col gap-3">
                {board.github ? (
                  <p className="text-sm text-[var(--foreground)]">Connected to <strong>{board.github.owner}/{board.github.repo}</strong>.</p>
                ) : (
                  <p className="text-sm text-[var(--muted)]">No repository connected. Connecting a repo lets agents push their work to GitHub. A repository can be connected to only one board.</p>
                )}
                <div><Button variant="primary" onClick={() => setGithubOpen(true)}>{board.github ? 'Manage connection' : 'Connect GitHub repo'}</Button></div>
              </div>
            )}

            {tab === 'workspace' && <WorkspaceTab board={board} isOwner={isOwner} />}

            {tab === 'danger' && isOwner && (
              <div className="flex flex-col gap-5">
                <DangerRow
                  title="Reset board"
                  desc="Delete all data, artifacts, run history, and workspace files. Items return to Backlog; connections and roles are kept."
                  action={<Button variant="danger" onClick={() => setResetOpen(true)}>Reset board</Button>}
                />
                <DangerRow
                  title="Delete board"
                  desc="Permanently delete this board and all its items. This cannot be undone."
                  action={<Button variant="danger" onClick={() => setDeleteOpen(true)}>Delete board</Button>}
                />
              </div>
            )}
          </div>
        </div>
      </Modal>

      {githubOpen && (
        <GitHubConnectModal board={board} onClose={() => setGithubOpen(false)} onUpdated={b => { onBoardUpdated(b); setGithubOpen(false); }} />
      )}
      {cloneOpen && <CloneBoardDialog board={board} onClose={() => setCloneOpen(false)} />}
      {resetOpen && <ResetBoardDialog board={board} onClose={() => setResetOpen(false)} onDone={() => { setResetOpen(false); onClose(); router.refresh(); }} />}

      <Modal open={deleteOpen} onClose={() => setDeleteOpen(false)} title="Delete board" width={400} z={1300}
        footer={
          <>
            <Button variant="ghost" onClick={() => setDeleteOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleDelete} disabled={deleting} style={{ background: '#e2445c', borderColor: '#e2445c' }}>
              {deleting ? 'Deleting…' : 'Delete board'}
            </Button>
          </>
        }>
        <p className="text-sm text-[var(--foreground)]">This will permanently delete <strong>{board.name}</strong> and all its tasks. This cannot be undone.</p>
      </Modal>
    </>
  );
}

function DangerRow({ title, desc, action }: { title: string; desc: string; action: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-4 border border-[#e2445c33] rounded-lg p-3">
      <div className="min-w-0">
        <div className="text-sm font-medium text-[var(--foreground)]">{title}</div>
        <div className="text-xs text-[var(--muted)]">{desc}</div>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  );
}
