'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Board, BoardWorkspaceStatusDto } from '@/lib/types';

// Friendly label + dot color for the live ACI state.
const STATE_UI: Record<string, { label: string; color: string }> = {
  Running:      { label: 'Running',         color: '#00c875' },
  Provisioning: { label: 'Provisioning…',   color: '#fdab3d' },
  Stopped:      { label: 'Stopped',         color: '#c4c4c4' },
  Failed:       { label: 'Failed',          color: '#e2445c' },
  NotFound:     { label: 'Not provisioned', color: '#c4c4c4' },
  Unknown:      { label: 'Unknown',         color: '#c4c4c4' },
};

export function WorkspaceTab({ board, isOwner }: { board: Board; isOwner: boolean }) {
  const [info, setInfo] = useState<BoardWorkspaceStatusDto | null>(null);
  const [busy, setBusy] = useState<'' | 'start' | 'restart' | 'terminate'>('');
  const alive = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const dto = await api.boards.workspace.get(board.id);
      if (alive.current) setInfo(dto);
    } catch { /* keep last */ }
  }, [board.id]);

  // Poll while the tab is open.
  useEffect(() => {
    alive.current = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- refresh() only setStates after an await (async), not synchronously
    void refresh();
    const t = setInterval(refresh, 5000);
    return () => { alive.current = false; clearInterval(t); };
  }, [refresh]);

  const run = async (kind: 'start' | 'restart' | 'terminate', fn: () => Promise<BoardWorkspaceStatusDto>) => {
    setBusy(kind);
    try { const result = await fn(); if (alive.current) setInfo(result); }
    catch (e) { toast(e instanceof Error && e.message.includes('409') ? 'Stop active runs first.' : `Could not ${kind} workspace`, 'error'); }
    finally { if (alive.current) setBusy(''); }
  };

  const state = info?.azureState ?? 'Unknown';
  const ui = STATE_UI[state] ?? STATE_UI.Unknown;
  const isUp = state === 'Running' || state === 'Provisioning';

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-2.5">
        <span className="inline-flex w-2.5 h-2.5 rounded-full" style={{ background: ui.color }} />
        <span className="text-sm font-medium text-[var(--foreground)]">{ui.label}</span>
        {info?.hasActiveRuns && <span className="text-[11px] text-[var(--muted)]">· active run in progress</span>}
      </div>

      <dl className="grid grid-cols-[120px_1fr] gap-y-1.5 text-[13px]">
        <dt className="text-[var(--muted)]">Container</dt><dd className="text-[var(--foreground)] truncate">{info?.containerName ?? '—'}</dd>
        <dt className="text-[var(--muted)]">Endpoint</dt><dd className="text-[var(--foreground)] truncate">{info?.endpoint ?? '—'}</dd>
        <dt className="text-[var(--muted)]">Last used</dt><dd className="text-[var(--foreground)]">{info?.lastUsedAt ? new Date(info.lastUsedAt).toLocaleString() : '—'}</dd>
        <dt className="text-[var(--muted)]">Auto-shutdown</dt><dd className="text-[var(--foreground)]">{info?.idleShutdownAt && isUp ? new Date(info.idleShutdownAt).toLocaleTimeString() : '—'}</dd>
      </dl>

      {isOwner ? (
        <div className="flex items-center gap-2 pt-1">
          {!isUp && (
            <Button variant="primary" disabled={!!busy} onClick={() => run('start', () => api.boards.workspace.start(board.id))}>
              {busy === 'start' ? 'Starting…' : 'Start'}
            </Button>
          )}
          {isUp && (
            <Button disabled={!!busy || info?.hasActiveRuns} onClick={() => run('restart', () => api.boards.workspace.restart(board.id))}>
              {busy === 'restart' ? 'Restarting…' : 'Restart'}
            </Button>
          )}
          {isUp && (
            <Button variant="danger" disabled={!!busy || info?.hasActiveRuns} onClick={() => run('terminate', () => api.boards.workspace.terminate(board.id))}>
              {busy === 'terminate' ? 'Terminating…' : 'Terminate'}
            </Button>
          )}
          {info?.hasActiveRuns && isUp && <span className="text-[11px] text-[var(--muted)]">Stop active runs to restart/terminate.</span>}
        </div>
      ) : (
        <p className="text-[11px] text-[var(--muted)]">Only the board owner can control the workspace.</p>
      )}
      <p className="text-[11px] text-[var(--muted)]">
        The workspace is a container that holds the board&apos;s files and runs its agents. It starts automatically on
        the first run and shuts down after about 10 minutes idle.
      </p>
    </div>
  );
}
