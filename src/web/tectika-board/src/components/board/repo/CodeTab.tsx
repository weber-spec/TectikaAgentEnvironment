'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { TreeEntry, FileContent } from '@/lib/types';
import { Icon } from '@/components/ui/icons';
import { languageForPath, highlightToHtml } from '@/lib/highlight';

export function CodeTab({ boardId, branch }: { boardId: string; branch: string }) {
  const [entries, setEntries] = useState<TreeEntry[]>([]);
  const [dir, setDir] = useState('');             // current directory path ('' = root)
  const [selected, setSelected] = useState<string | null>(null);
  const [loadingTree, setLoadingTree] = useState(true);

  // Reset navigation when the branch changes.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset navigation on branch change
    setDir(''); setSelected(null);
  }, [branch]);

  useEffect(() => {
    let live = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset loading state before async fetch
    setLoadingTree(true);
    api.repo.tree(boardId, branch, dir)
      .then(e => { if (live) setEntries(e); })
      .catch(() => { if (live) setEntries([]); })
      .finally(() => { if (live) setLoadingTree(false); });
    return () => { live = false; };
  }, [boardId, branch, dir]);

  const up = () => setDir(d => d.includes('/') ? d.slice(0, d.lastIndexOf('/')) : '');

  return (
    <div className="flex h-full">
      <div className="w-1/3 max-w-[320px] border-r border-[var(--border)] overflow-auto p-2 text-[13px]">
        {dir !== '' && (
          <button onClick={up} className="flex items-center gap-1 text-[var(--muted)] hover:text-[var(--foreground)] px-1 py-0.5"><Icon.chevronLeft size={14} /> ..</button>
        )}
        {loadingTree ? <div className="text-[var(--muted)] p-2">Loading…</div>
          : entries.length === 0 ? <div className="text-[var(--muted)] p-2">Empty.</div>
          : entries.map(e => (
            <button key={e.path} onClick={() => e.type === 'dir' ? setDir(e.path) : setSelected(e.path)}
              className={`flex items-center gap-1.5 w-full text-left px-1.5 py-1 rounded hover:bg-[var(--surface)] ${selected === e.path ? 'bg-[var(--surface)] text-[var(--primary)]' : 'text-[var(--foreground)]'}`}>
              <Icon.file size={14} className={e.type === 'dir' ? 'text-[var(--primary)]' : 'text-[var(--muted)]'} />
              <span className="truncate">{e.name}{e.type === 'dir' ? '/' : ''}</span>
            </button>
          ))}
      </div>
      <div className="flex-1 min-w-0 overflow-auto">
        {selected ? <FileViewer boardId={boardId} branch={branch} path={selected} /> : <div className="flex items-center justify-center h-full text-[var(--muted)] text-sm">Select a file to view.</div>}
      </div>
    </div>
  );
}

function FileViewer({ boardId, branch, path }: { boardId: string; branch: string; path: string }) {
  const [file, setFile] = useState<FileContent | null>(null);
  const [html, setHtml] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let live = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset state before async fetch
    setLoading(true); setHtml(null); setFile(null);
    api.repo.file(boardId, path, branch)
      .then(async f => {
        if (!live) return;
        setFile(f);
        if (!f.isBinary && f.text != null) {
          const h = await highlightToHtml(f.text, languageForPath(f.path));
          if (live) setHtml(h);
        }
      })
      .catch(() => { if (live) setFile(null); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [boardId, branch, path]);

  if (loading) return <div className="p-4 text-[var(--muted)] text-sm">Loading file…</div>;
  if (!file) return <div className="p-4 text-[var(--muted)] text-sm">Could not load this file.</div>;
  if (file.isBinary || file.text == null) {
    return <div className="p-4 text-[var(--muted)] text-sm">Binary or large file ({file.size} bytes) — not shown.</div>;
  }
  return (
    <div className="text-[12.5px]">
      <div className="px-3 py-1.5 border-b border-[var(--border)] text-[11px] text-[var(--muted)] font-mono">{file.path}</div>
      {html
        ? <div className="[&_pre]:!m-0 [&_pre]:!p-3 [&_pre]:overflow-auto [&_pre]:text-[12.5px]" dangerouslySetInnerHTML={{ __html: html }} />
        : <pre className="font-mono p-3 overflow-auto whitespace-pre text-[var(--foreground)]">{file.text}</pre>}
    </div>
  );
}
