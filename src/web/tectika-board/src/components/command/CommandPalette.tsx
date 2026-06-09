'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useRouter } from 'next/navigation';
import { api } from '@/lib/api';
import type { Board } from '@/lib/types';
import { useSettings } from '@/lib/settings-context';
import { fuzzyScore } from '@/lib/format';
import { Icon, type IconName } from '@/components/ui/icons';

interface Command { id: string; label: string; hint?: string; icon: IconName; run: () => void; group: string }

export function CommandPalette() {
  const router = useRouter();
  const { settings, updateSettings } = useSettings();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const [boards, setBoards] = useState<Board[]>([]);
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setOpen(o => !o); }
      if (e.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // eslint-disable-next-line react-hooks/set-state-in-effect -- reset palette state when it opens
  useEffect(() => { if (open) { api.boards.list().then(setBoards).catch(() => {}); setQ(''); setActive(0); setTimeout(() => inputRef.current?.focus(), 30); } }, [open]);

  const commands: Command[] = useMemo(() => {
    const go = (path: string) => () => { router.push(path); setOpen(false); };
    const nav: Command[] = [
      { id: 'boards', label: 'Go to Boards', icon: 'board', run: go('/boards'), group: 'Navigate' },
      { id: 'agents', label: 'Go to Agents', icon: 'robot', run: go('/agents'), group: 'Navigate' },
      { id: 'approvals', label: 'Go to Approvals', icon: 'approvals', run: go('/approvals'), group: 'Navigate' },
      { id: 'dash', label: 'Go to Dashboards', icon: 'chart', run: go('/dashboards'), group: 'Navigate' },
      { id: 'analytics', label: 'Go to Analytics', icon: 'chart', run: go('/analytics'), group: 'Navigate' },
      { id: 'settings', label: 'Open Settings', icon: 'settings', run: go('/settings'), group: 'Navigate' },
    ];
    const actions: Command[] = [
      { id: 'theme', label: `Switch to ${settings.theme === 'dark' ? 'light' : 'dark'} mode`, icon: 'bolt', run: () => { updateSettings({ theme: settings.theme === 'dark' ? 'light' : 'dark' }); setOpen(false); }, group: 'Actions' },
      { id: 'newboard', label: 'Create new board', icon: 'plus', run: () => { router.push('/boards?new=1'); setOpen(false); }, group: 'Actions' },
    ];
    const boardCmds: Command[] = boards.map(b => ({ id: `b-${b.id}`, label: b.name, hint: 'Open board', icon: 'board', run: go(`/boards/${b.id}`), group: 'Boards' }));
    return [...boardCmds, ...nav, ...actions];
  }, [boards, router, settings.theme, updateSettings]);

  const filtered = useMemo(() => {
    if (!q) return commands;
    return commands.map(c => ({ c, s: fuzzyScore(c.label, q) })).filter(x => x.s > 0).sort((a, b) => b.s - a.s).map(x => x.c);
  }, [commands, q]);

  // eslint-disable-next-line react-hooks/set-state-in-effect -- reset highlighted row when the query changes
  useEffect(() => { setActive(0); }, [q]);

  if (!open) return null;

  const onKeyNav = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(filtered.length - 1, a + 1)); }
    if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(0, a - 1)); }
    if (e.key === 'Enter') { e.preventDefault(); filtered[active]?.run(); }
  };

  return createPortal(
    <div className="fixed inset-0 z-[1500] flex items-start justify-center pt-[12vh] px-4" style={{ background: 'rgba(0,0,0,0.4)' }} onMouseDown={() => setOpen(false)}>
      <div className="w-full max-w-[560px] bg-[var(--background)] rounded-xl shadow-2xl border border-[var(--border)] overflow-hidden animate-scale-in" onMouseDown={e => e.stopPropagation()}>
        <div className="flex items-center gap-2 px-4 border-b border-[var(--border)]">
          <Icon.search size={18} className="text-[var(--muted)]" />
          <input ref={inputRef} value={q} onChange={e => setQ(e.target.value)} onKeyDown={onKeyNav}
            placeholder="Search boards, pages, actions…" className="flex-1 bg-transparent outline-none py-3.5 text-[15px] text-[var(--foreground)]" />
          <kbd className="text-[10px] text-[var(--muted)] border border-[var(--border)] rounded px-1.5 py-0.5">ESC</kbd>
        </div>
        <div className="max-h-[50vh] overflow-auto py-1">
          {filtered.length === 0 && <div className="px-4 py-8 text-center text-sm text-[var(--muted)]">No matches</div>}
          {filtered.map((c, i) => {
            const showGroup = i === 0 || filtered[i - 1].group !== c.group;
            const I = Icon[c.icon];
            return (
              <React.Fragment key={c.id}>
                {showGroup && <div className="px-4 pt-2 pb-1 text-[10px] uppercase tracking-wide text-[var(--muted)] font-semibold">{c.group}</div>}
                <button onMouseEnter={() => setActive(i)} onClick={c.run}
                  className={`w-full flex items-center gap-3 px-4 py-2 text-left ${i === active ? 'bg-[var(--primary-light)]' : ''}`}>
                  <I size={16} className="text-[var(--muted)]" />
                  <span className="flex-1 text-sm text-[var(--foreground)]">{c.label}</span>
                  {c.hint && <span className="text-[11px] text-[var(--muted)]">{c.hint}</span>}
                </button>
              </React.Fragment>
            );
          })}
        </div>
      </div>
    </div>,
    document.body,
  );
}
