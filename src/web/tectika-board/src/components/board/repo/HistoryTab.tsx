'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { CommitInfo } from '@/lib/types';

export function HistoryTab({ boardId, branch }: { boardId: string; branch: string }) {
  const [commits, setCommits] = useState<CommitInfo[] | null>(null);

  useEffect(() => {
    let live = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset on board/branch change
    setCommits(null);
    api.repo.commits(boardId, branch).then(c => { if (live) setCommits(c); }).catch(() => { if (live) setCommits([]); });
    return () => { live = false; };
  }, [boardId, branch]);

  if (commits === null) return <div className="p-4 text-[var(--muted)] text-sm">Loading history…</div>;
  if (commits.length === 0) return <div className="p-4 text-[var(--muted)] text-sm">No commits on this branch.</div>;
  return (
    <div className="overflow-auto h-full divide-y divide-[var(--border)]">
      {commits.map(c => (
        <a key={c.sha} href={c.url} target="_blank" rel="noreferrer" className="block px-4 py-2.5 hover:bg-[var(--surface)]">
          <div className="text-[13px] text-[var(--foreground)] truncate">{c.message.split('\n')[0]}</div>
          <div className="text-[11px] text-[var(--muted)] font-mono">{c.sha.slice(0, 7)} · {c.author} · {new Date(c.date).toLocaleDateString()}</div>
        </a>
      ))}
    </div>
  );
}
