'use client';

import React, { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { PreviewSession } from '@/lib/types';

export function PreviewTab({ boardId, branch }: { boardId: string; branch: string }) {
  const [session, setSession] = useState<PreviewSession | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    api.preview.get(boardId).then(s => { if (live) setSession(s); }).catch(() => { if (live) setSession(null); });
    return () => { live = false; };
  }, [boardId]);

  // Poll while provisioning.
  useEffect(() => {
    if (session?.status !== 'Provisioning') return;
    const id = setInterval(() => {
      api.preview.get(boardId).then(s => { if (s) setSession(s); }).catch(() => {});
    }, 2000);
    return () => clearInterval(id);
  }, [session?.status, boardId]);

  // Keep-alive heartbeat while running.
  useEffect(() => {
    if (session?.status !== 'Running') return;
    const id = setInterval(() => {
      api.preview.heartbeat(boardId).then(setSession).catch(() => {});
    }, 60000);
    return () => clearInterval(id);
  }, [session?.status, boardId]);

  const start = async () => {
    setBusy(true);
    try { setSession(await api.preview.start(boardId, branch)); }
    finally { setBusy(false); }
  };
  const stop = async () => {
    setBusy(true);
    try { await api.preview.stop(boardId); setSession(null); }
    finally { setBusy(false); }
  };

  if (!session || session.status === 'Stopped') {
    return (
      <div className="p-4 text-[13px] text-[var(--foreground)]">
        <p className="mb-3 text-[var(--muted)]">Preview branch <b className="text-[var(--foreground)]">{branch}</b> as a running app.</p>
        <button onClick={start} disabled={busy}
          className="px-3 py-1.5 rounded-md bg-[var(--primary)] text-white text-[13px] font-medium disabled:opacity-50">
          {busy ? 'Starting…' : '▶ Start preview'}
        </button>
      </div>
    );
  }

  if (session.status === 'Provisioning') {
    return (
      <div className="flex items-center justify-center h-full text-sm text-[var(--muted)] px-4 text-center">
        ⏳ Starting preview of <b className="mx-1 text-[var(--foreground)]">{session.branch}</b> — cloning &amp; installing… (this can take a minute)
      </div>
    );
  }

  if (session.status === 'Failed') {
    return (
      <div className="p-4 text-[13px] text-[var(--foreground)]">
        <p className="mb-3 text-[var(--muted)]">⚠️ Preview failed: {session.error}</p>
        <button onClick={start} disabled={busy}
          className="px-3 py-1.5 rounded-md bg-[var(--primary)] text-white text-[13px] font-medium disabled:opacity-50">
          {busy ? 'Retrying…' : 'Retry'}
        </button>
      </div>
    );
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-3 px-4 py-2 border-b border-[var(--border)] text-[13px]">
        <span className="font-medium text-[#22c55e]">● live · {session.branch}</span>
        <a href={session.url} target="_blank" rel="noreferrer"
          className="text-[var(--primary)] hover:underline font-medium">Open ↗</a>
        <button onClick={() => navigator.clipboard.writeText(session.url ?? '')}
          className="text-[var(--muted)] hover:text-[var(--foreground)] font-medium">Copy link</button>
        <span className="text-[11px] text-[var(--muted)]">The app may need a few more seconds to finish booting.</span>
        <div className="flex-1" />
        <button onClick={stop} disabled={busy}
          className="px-2.5 py-1 rounded-md border border-[var(--border)] text-[var(--foreground)] font-medium hover:bg-[var(--surface)] disabled:opacity-50">
          ■ Stop
        </button>
      </div>
      <iframe title="preview" src={session.url} className="flex-1 w-full border-0" />
    </div>
  );
}
