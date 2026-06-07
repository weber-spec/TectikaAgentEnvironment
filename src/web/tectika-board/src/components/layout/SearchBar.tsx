'use client';

import { useState, useRef, useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { api } from '@/lib/api';
import { useSettings } from '@/lib/settings-context';
import { Pill } from '@/components/ui/primitives';
import { STATUS_CONFIG } from '@/lib/palette';
import type { Board, AgentTask } from '@/lib/types';

type SearchResult =
  | { kind: 'board'; board: Board }
  | { kind: 'task'; task: AgentTask; boardName: string };

export function SearchBar() {
  const { t } = useSettings();
  const router = useRouter();

  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [boards, setBoards] = useState<Board[]>([]);
  const [taskRows, setTaskRows] = useState<Array<{ task: AgentTask; boardName: string }>>([]);
  const [activeIndex, setActiveIndex] = useState(-1);

  const inputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const fetchedRef = useRef(false);

  // Close on outside click
  useEffect(() => {
    function handler(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  async function loadData() {
    if (fetchedRef.current) return;
    fetchedRef.current = true;
    setLoading(true);
    try {
      const allBoards = await api.boards.list();
      setBoards(allBoards);
      const settled = await Promise.allSettled(
        allBoards.map(b =>
          api.tasks.list(b.id).then(tasks =>
            tasks.map(task => ({ task, boardName: b.name }))
          )
        )
      );
      setTaskRows(settled.flatMap(r => r.status === 'fulfilled' ? r.value : []));
    } catch {}
    setLoading(false);
  }

  // Build filtered results
  const q = query.trim().toLowerCase();
  const results: SearchResult[] = q.length === 0 ? [] : [
    ...boards
      .filter(b =>
        b.name.toLowerCase().includes(q) ||
        (b.description ?? '').toLowerCase().includes(q)
      )
      .slice(0, 4)
      .map(b => ({ kind: 'board' as const, board: b })),
    ...taskRows
      .filter(({ task }) => task.title.toLowerCase().includes(q))
      .slice(0, 5)
      .map(({ task, boardName }) => ({ kind: 'task' as const, task, boardName })),
  ];

  function navigate(result: SearchResult) {
    if (result.kind === 'board') {
      router.push(`/boards/${result.board.id}`);
    } else {
      router.push(`/boards/${result.task.boardId}`);
    }
    setQuery('');
    setOpen(false);
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Escape') {
      setOpen(false);
      inputRef.current?.blur();
      return;
    }
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActiveIndex(i => Math.min(i + 1, results.length - 1));
      return;
    }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActiveIndex(i => Math.max(i - 1, -1));
      return;
    }
    if (e.key === 'Enter' && activeIndex >= 0 && results[activeIndex]) {
      navigate(results[activeIndex]);
    }
  }

  const boardResults = results.filter(r => r.kind === 'board') as Extract<SearchResult, { kind: 'board' }>[];
  const taskResults  = results.filter(r => r.kind === 'task')  as Extract<SearchResult, { kind: 'task' }>[];

  return (
    <div ref={containerRef} className="relative flex-1 max-w-sm">
      {/* Input */}
      <div
        className="flex items-center gap-2 border rounded-full px-3 py-1.5 text-sm transition-all duration-150"
        style={{
          background: 'var(--surface)',
          borderColor: open ? 'var(--primary)' : 'var(--border)',
          boxShadow: open ? '0 0 0 3px var(--primary-light)' : 'none',
        }}
      >
        {loading ? (
          <div className="w-3.5 h-3.5 border-2 border-[var(--primary)] border-t-transparent rounded-full animate-spin shrink-0" />
        ) : (
          <svg width="14" height="14" viewBox="0 0 20 20" fill="none" className="shrink-0 text-[var(--muted)]">
            <circle cx="9" cy="9" r="6" stroke="currentColor" strokeWidth="2"/>
            <path d="m15 15 3 3" stroke="currentColor" strokeWidth="2" strokeLinecap="round"/>
          </svg>
        )}
        <input
          ref={inputRef}
          type="text"
          value={query}
          placeholder={t('search')}
          className="flex-1 bg-transparent outline-none text-[var(--foreground)] placeholder:text-[var(--muted)] text-sm min-w-0"
          onFocus={() => { setOpen(true); loadData(); setActiveIndex(-1); }}
          onChange={e => { setQuery(e.target.value); setActiveIndex(-1); }}
          onKeyDown={handleKeyDown}
        />
        {query && (
          <button
            onClick={() => { setQuery(''); inputRef.current?.focus(); }}
            className="shrink-0 text-[var(--muted)] hover:text-[var(--foreground)] transition-colors"
          >
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none">
              <path d="M18 6L6 18M6 6l12 12" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"/>
            </svg>
          </button>
        )}
      </div>

      {/* Dropdown */}
      {open && query.length > 0 && (
        <div className="absolute top-[calc(100%+6px)] left-0 right-0 z-50 rounded-xl shadow-2xl border border-[var(--border)] overflow-hidden animate-scale-in"
          style={{ background: 'var(--background)', minWidth: '320px' }}
        >
          {results.length === 0 ? (
            <div className="px-4 py-8 text-center">
              <div className="text-2xl mb-2">🔍</div>
              <p className="text-sm text-[var(--muted)]">No results for &ldquo;{query}&rdquo;</p>
            </div>
          ) : (
            <>
              {/* Boards */}
              {boardResults.length > 0 && (
                <div>
                  <p className="px-3 pt-3 pb-1 text-[10px] uppercase tracking-widest font-semibold text-[var(--muted-2)]">
                    {t('boards')}
                  </p>
                  {boardResults.map(r => {
                    const idx = results.indexOf(r);
                    return (
                      <button
                        key={r.board.id}
                        onClick={() => navigate(r)}
                        onMouseEnter={() => setActiveIndex(idx)}
                        className="w-full flex items-center gap-3 px-3 py-2.5 text-left transition-colors"
                        style={{ background: activeIndex === idx ? 'var(--primary-light)' : 'transparent' }}
                      >
                        <span className="text-base shrink-0">📋</span>
                        <div className="min-w-0 flex-1">
                          <p className="text-sm font-medium text-[var(--foreground)] truncate">{r.board.name}</p>
                          {r.board.description && (
                            <p className="text-xs text-[var(--muted)] truncate">{r.board.description}</p>
                          )}
                        </div>
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" className="shrink-0 text-[var(--muted-2)]">
                          <path d="M9 18l6-6-6-6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
                        </svg>
                      </button>
                    );
                  })}
                </div>
              )}

              {/* Tasks */}
              {taskResults.length > 0 && (
                <div className={boardResults.length > 0 ? 'border-t border-[var(--border)]' : ''}>
                  <p className="px-3 pt-3 pb-1 text-[10px] uppercase tracking-widest font-semibold text-[var(--muted-2)]">
                    Tasks
                  </p>
                  {taskResults.map(r => {
                    const idx = results.indexOf(r);
                    return (
                      <button
                        key={r.task.id}
                        onClick={() => navigate(r)}
                        onMouseEnter={() => setActiveIndex(idx)}
                        className="w-full flex items-center gap-3 px-3 py-2.5 text-left transition-colors"
                        style={{ background: activeIndex === idx ? 'var(--primary-light)' : 'transparent' }}
                      >
                        <span className="text-base shrink-0">⚡</span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium text-[var(--foreground)] truncate">{r.task.title}</p>
                          <p className="text-xs text-[var(--muted)] truncate">{r.boardName}</p>
                        </div>
                        <Pill label={STATUS_CONFIG[r.task.status].label} hex={STATUS_CONFIG[r.task.status].hex} />
                      </button>
                    );
                  })}
                </div>
              )}

              {/* Footer hint */}
              <div className="px-3 py-2 border-t border-[var(--border)] flex items-center gap-3 text-[10px] text-[var(--muted-2)]">
                <span>↑↓ navigate</span>
                <span>↵ open</span>
                <span>Esc close</span>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
