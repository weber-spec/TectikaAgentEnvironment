'use client';

import { useState } from 'react';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Board } from '@/lib/types';

export function ResetBoardDialog({ board, onClose, onDone }: { board: Board; onClose: () => void; onDone: () => void }) {
  const connected = !!board.github;
  const [clearRepo, setClearRepo] = useState(false);
  const [confirm, setConfirm] = useState('');
  const [resetting, setResetting] = useState(false);
  const armed = confirm.trim() === board.name;

  const handleReset = async () => {
    if (!armed) return;
    setResetting(true);
    try {
      await api.boards.reset(board.id, connected && clearRepo);
      toast('Board reset', 'success');
      onDone();
    } catch {
      toast('Could not reset board', 'error');
      setResetting(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      title="Reset board"
      width={460}
      z={1300}
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={resetting}>Cancel</Button>
          <Button variant="primary" onClick={handleReset} disabled={!armed || resetting}
            style={{ background: '#e2445c', borderColor: '#e2445c' }}>
            {resetting ? 'Resetting…' : 'Reset board'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <p className="text-sm text-[var(--foreground)]">
          This permanently deletes <strong>all data, artifacts, run history, and workspace files</strong> for
          <strong> {board.name}</strong>, destroys its workspace container, and returns every item to Backlog.
          Items, their connections, and agent roles are kept. This cannot be undone.
        </p>
        {connected && (
          <label className="flex items-start gap-2.5 cursor-pointer select-none">
            <input type="checkbox" checked={clearRepo} onChange={e => setClearRepo(e.target.checked)} className="mt-0.5" />
            <span className="text-sm text-[var(--foreground)]">
              Also clear the repository
              <span className="block text-xs text-[var(--muted)]">
                Disconnects <strong>{board.github?.owner}/{board.github?.repo}</strong> and makes this a standalone board.
                The GitHub remote itself is not modified.
              </span>
            </span>
          </label>
        )}
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-[var(--muted)]">Type <strong>{board.name}</strong> to confirm</label>
          <input
            value={confirm}
            onChange={e => setConfirm(e.target.value)}
            className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[#e2445c]"
          />
        </div>
      </div>
    </Modal>
  );
}
