'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { cloneBoardConfig } from '@/lib/board-config';
import { toast } from '@/lib/toast';
import type { Board } from '@/lib/types';

export function CloneBoardDialog({ board, onClose }: { board: Board; onClose: () => void }) {
  const router = useRouter();
  const [name, setName] = useState(`Copy of ${board.name}`);
  const [includeData, setIncludeData] = useState(false);
  const [cloning, setCloning] = useState(false);

  const handleClone = async () => {
    if (!name.trim()) return;
    setCloning(true);
    try {
      const created = await api.boards.clone(board.id, { name: name.trim(), includeData });
      cloneBoardConfig(board.id, created.id);   // carry views/columns layout to the clone
      toast('Board cloned', 'success');
      router.push(`/boards/${created.id}`);
    } catch {
      toast('Could not clone board', 'error');
      setCloning(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      title="Clone board"
      width={460}
      z={1300}
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={cloning}>Cancel</Button>
          <Button variant="primary" onClick={handleClone} disabled={!name.trim() || cloning}>
            {cloning ? 'Cloning…' : 'Clone board'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-[var(--muted)]">New board name <span className="text-[#e2445c]">*</span></label>
          <input
            autoFocus
            value={name}
            onChange={e => setName(e.target.value)}
            className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)]"
          />
        </div>
        <label className="flex items-start gap-2.5 cursor-pointer select-none">
          <input type="checkbox" checked={includeData} onChange={e => setIncludeData(e.target.checked)} className="mt-0.5" />
          <span className="text-sm text-[var(--foreground)]">
            Include data
            <span className="block text-xs text-[var(--muted)]">
              Copy each item&apos;s latest deliverable and the workspace files, keeping item statuses. Off ⇒ items start in Backlog with an empty workspace.
            </span>
          </span>
        </label>
        <p className="text-[11px] text-[var(--muted)]">The clone is standalone — it is not connected to any GitHub repository.</p>
      </div>
    </Modal>
  );
}
