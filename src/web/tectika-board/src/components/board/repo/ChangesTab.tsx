'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { CompareResult, DiffFile } from '@/lib/types';
import { parseUnifiedDiff } from '@/lib/diff';

export function ChangesTab({ boardId, base, head }: { boardId: string; base: string; head: string }) {
  const [result, setResult] = useState<CompareResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset on base/head change
    setResult(null); setError(null); setSelected(null);
    api.repo.compare(boardId, base, head)
      .then(r => { if (live) { setResult(r); setSelected(r.files[0]?.path ?? null); } })
      .catch(() => { if (live) setError('Could not load the diff for this branch.'); });
    return () => { live = false; };
  }, [boardId, base, head]);

  if (error) return <div className="p-4 text-[var(--muted)] text-sm">{error}</div>;
  if (!result) return <div className="p-4 text-[var(--muted)] text-sm">Loading changes…</div>;
  if (result.filesChanged === 0) return <div className="p-4 text-[var(--muted)] text-sm">No changes between {base} and {head}.</div>;

  const file = result.files.find(f => f.path === selected) ?? null;
  return (
    <div className="flex h-full">
      <div className="w-1/3 max-w-[320px] border-r border-[var(--border)] overflow-auto p-2 text-[12px]">
        <div className="px-1 py-1 text-[11px] text-[var(--muted)] font-mono">{head} vs {base} · {result.filesChanged} files</div>
        {result.files.map(f => (
          <button key={f.path} onClick={() => setSelected(f.path)}
            className={`flex items-center gap-2 w-full text-left px-1.5 py-1 rounded hover:bg-[var(--surface)] ${selected === f.path ? 'bg-[var(--surface)] text-[var(--primary)]' : 'text-[var(--foreground)]'}`}>
            <span className="truncate flex-1 font-mono">{f.path}</span>
            <span className="text-emerald-500 text-[11px]">+{f.additions}</span>
            <span className="text-red-500 text-[11px]">−{f.deletions}</span>
          </button>
        ))}
      </div>
      <div className="flex-1 min-w-0 overflow-auto">
        {file ? <DiffView file={file} /> : <div className="flex items-center justify-center h-full text-[var(--muted)] text-sm">Select a file.</div>}
      </div>
    </div>
  );
}

function DiffView({ file }: { file: DiffFile }) {
  if (file.isBinary || file.patch == null) {
    return <div className="p-4 text-[var(--muted)] text-sm">Binary file — not shown.</div>;
  }
  const lines = parseUnifiedDiff(file.patch);
  return (
    <div>
      <div className="px-3 py-1.5 border-b border-[var(--border)] text-[11px] text-[var(--muted)] font-mono">{file.path}</div>
      <pre className="font-mono text-[12px] overflow-auto">
        {lines.map((l, i) => (
          <div key={i} className={
            l.type === 'add' ? 'bg-emerald-500/10 text-emerald-300' :
            l.type === 'del' ? 'bg-red-500/10 text-red-300' :
            l.type === 'hunk' ? 'text-[var(--muted)] bg-[var(--surface)]' : 'text-[var(--foreground)]'}>
            <span className="px-3">{l.type === 'add' ? '+' : l.type === 'del' ? '−' : ' '}{l.text}</span>
          </div>
        ))}
      </pre>
    </div>
  );
}
