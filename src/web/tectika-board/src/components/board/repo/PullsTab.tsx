'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { PullRequestInfo } from '@/lib/types';

export function PullsTab({ boardId }: { boardId: string }) {
  const [state, setState] = useState<'open' | 'closed' | 'all'>('open');
  const [prs, setPrs] = useState<PullRequestInfo[] | null>(null);

  useEffect(() => {
    let live = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset on boardId/state change
    setPrs(null);
    api.repo.pulls(boardId, state).then(p => { if (live) setPrs(p); }).catch(() => { if (live) setPrs([]); });
    return () => { live = false; };
  }, [boardId, state]);

  return (
    <div className="flex flex-col h-full">
      <div className="flex gap-2 px-4 py-2 border-b border-[var(--border)]">
        {(['open', 'closed', 'all'] as const).map(s => (
          <button key={s} onClick={() => setState(s)}
            className={`text-[12px] px-2 py-0.5 rounded capitalize ${state === s ? 'bg-[var(--primary)] text-white' : 'text-[var(--muted)] hover:text-[var(--foreground)]'}`}>{s}</button>
        ))}
      </div>
      <div className="flex-1 overflow-auto divide-y divide-[var(--border)]">
        {prs === null ? <div className="p-4 text-[var(--muted)] text-sm">Loading pull requests…</div>
          : prs.length === 0 ? <div className="p-4 text-[var(--muted)] text-sm">No {state} pull requests.</div>
          : prs.map(pr => (
            <a key={pr.number} href={pr.url} target="_blank" rel="noreferrer" className="block px-4 py-2.5 hover:bg-[var(--surface)]">
              <div className="text-[13px] text-[var(--foreground)] truncate">#{pr.number} {pr.title}</div>
              <div className="text-[11px] text-[var(--muted)] font-mono">{pr.state} · {pr.head} → {pr.base} · {pr.author}</div>
            </a>
          ))}
      </div>
    </div>
  );
}
