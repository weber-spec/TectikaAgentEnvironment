'use client';

import React, { useEffect, useState } from 'react';
import { api, ApiError } from '@/lib/api';
import type { RepoMeta } from '@/lib/types';
import { Icon } from '@/components/ui/icons';
import { CodeTab } from './CodeTab';
import { HistoryTab } from './HistoryTab';
import { PullsTab } from './PullsTab';
import { ChangesTab } from './ChangesTab';

type Sub = 'code' | 'history' | 'pulls' | 'changes';

export function RepoView({ boardId, onConnectGitHub, changesTarget }: { boardId: string; onConnectGitHub: () => void; changesTarget?: string }) {
  const [sub, setSub] = useState<Sub>('code');
  const [meta, setMeta] = useState<RepoMeta | null>(null);
  const [branch, setBranch] = useState<string>('');
  const [branches, setBranches] = useState<string[]>([]);
  const [changesHead, setChangesHead] = useState<string | undefined>();
  const [notConnected, setNotConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    // Reset all repo-derived state on board change so a previous board's repo never
    // shows under a new board, and we always adopt the new board's default branch.
    // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional reset on boardId change
    setError(null); setNotConnected(false); setMeta(null); setBranches([]); setBranch('');
    (async () => {
      try {
        const m = await api.repo.meta(boardId);
        if (!live) return;
        setMeta(m); setBranch(m.defaultBranch);
        const bs = await api.repo.branches(boardId);
        if (!live) return;
        setBranches(bs.map(x => x.name));
      } catch (e) {
        if (!live) return;
        if (e instanceof ApiError && e.status === 409) setNotConnected(true);
        else setError(e instanceof Error ? e.message : 'Failed to load repository.');
      }
    })();
    return () => { live = false; };
  }, [boardId]);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- deep-link: copy prop into local state on change
    if (changesTarget) { setChangesHead(changesTarget); setSub('changes'); }
  }, [changesTarget]);

  if (notConnected) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-3 text-[var(--muted)]">
        <Icon.warning size={32} />
        <p className="text-sm">No GitHub repository is connected to this board.</p>
        <button onClick={onConnectGitHub} className="px-3 py-1.5 rounded-md bg-[var(--primary)] text-white text-[13px] font-medium">Connect a GitHub repo</button>
      </div>
    );
  }
  if (error) {
    return <div className="flex flex-col items-center justify-center h-full gap-2 text-[var(--muted)]"><Icon.warning size={28} /><p className="text-sm">{error}</p></div>;
  }
  if (!meta) {
    return <div className="flex items-center justify-center h-full text-[var(--muted)] text-sm">Loading repository…</div>;
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-4 px-4 py-2 border-b border-[var(--border)]">
        {(['code', 'history', 'pulls'] as Sub[]).map(s => (
          <button key={s} onClick={() => setSub(s)}
            className={`text-[13px] font-medium border-b-2 -mb-2.5 pb-2 ${sub === s ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
            {s === 'code' ? 'Code' : s === 'history' ? 'History' : 'Pull Requests'}
          </button>
        ))}
        {changesHead && (
          <button onClick={() => setSub('changes')}
            className={`text-[13px] font-medium border-b-2 -mb-2.5 pb-2 ${sub === 'changes' ? 'border-[var(--primary)] text-[var(--primary)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
            Changes
          </button>
        )}
        <div className="flex-1" />
        <select value={branch} onChange={e => setBranch(e.target.value)}
          className="text-xs bg-[var(--surface)] rounded px-2 py-1 outline-none border border-[var(--border)] text-[var(--foreground)]">
          {branches.length === 0 && <option value={branch}>{branch}</option>}
          {branches.map(b => <option key={b} value={b}>{b}</option>)}
        </select>
      </div>
      <div className="flex-1 min-h-0">
        {sub === 'code' && <CodeTab boardId={boardId} branch={branch} />}
        {sub === 'history' && <HistoryTab boardId={boardId} branch={branch} />}
        {sub === 'pulls' && <PullsTab boardId={boardId} />}
        {sub === 'changes' && changesHead && <ChangesTab boardId={boardId} base={meta.defaultBranch} head={changesHead} />}
      </div>
    </div>
  );
}
