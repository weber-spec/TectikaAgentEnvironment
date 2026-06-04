'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { api } from '@/lib/api';
import { useSettings } from '@/lib/settings-context';
import type { Board } from '@/lib/types';

const BOARD_COLORS = [
  '#0073ea', '#00c875', '#fdab3d', '#e2445c',
  '#a25ddc', '#ff642e', '#66ccff', '#579bfc',
];

function boardColor(name: string) {
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = name.charCodeAt(i) + ((hash << 5) - hash);
  return BOARD_COLORS[Math.abs(hash) % BOARD_COLORS.length];
}

export default function BoardsPage() {
  const { t } = useSettings();
  const [boards, setBoards] = useState<Board[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.boards.list()
      .then(setBoards)
      .catch(() => setBoards([]))
      .finally(() => setLoading(false));
  }, []);

  const handleNew = async () => {
    const name = prompt(t('newBoard') + '?');
    if (!name) return;
    const board = await api.boards.create(name).catch(() => null);
    if (board) setBoards(prev => [...prev, board]);
  };

  return (
    <div className="flex flex-col h-full">
      {/* Page header */}
      <div className="bg-[var(--surface)] border-b border-[var(--border)] px-8 py-4 flex items-center justify-between">
        <h1 className="text-xl font-semibold text-[var(--foreground)]">{t('boards')}</h1>
        <button
          className="flex items-center gap-1.5 px-4 py-2 rounded-md text-sm font-semibold text-white transition-colors"
          style={{ background: 'var(--primary)' }}
          onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = 'var(--primary-hover)'; }}
          onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'var(--primary)'; }}
          onClick={handleNew}
        >
          <span className="text-base leading-none font-bold">+</span>
          {t('newBoard')}
        </button>
      </div>

      {/* Board list */}
      <div className="flex-1 overflow-auto">
        {loading ? (
          <div className="px-8 py-6 flex flex-col gap-2">
            {[...Array(4)].map((_, i) => (
              <div key={i} className="h-12 rounded bg-[var(--surface)] animate-pulse" />
            ))}
          </div>
        ) : boards.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-32 text-[var(--muted)]">
            <svg width="48" height="48" viewBox="0 0 24 24" fill="none" className="mb-4 text-[var(--muted-2)]">
              <rect x="3" y="3" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.5"/>
              <rect x="14" y="3" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.5"/>
              <rect x="3" y="14" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.5"/>
              <rect x="14" y="14" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.5"/>
            </svg>
            <p className="text-base font-semibold text-[var(--foreground)] mb-1">{t('noBoardsYet')}</p>
            <p className="text-sm mb-5">{t('noBoardsDesc')}</p>
            <button
              className="px-5 py-2 rounded-md text-sm font-semibold text-white"
              style={{ background: 'var(--primary)' }}
              onClick={handleNew}
            >
              + {t('newBoard')}
            </button>
          </div>
        ) : (
          <table className="w-full border-collapse">
            <thead>
              <tr className="border-b border-[var(--border)]">
                <th className="text-left px-8 py-2 text-[10px] font-semibold uppercase tracking-wider text-[var(--muted)] bg-[var(--surface)]">{t('colName')}</th>
                <th className="text-left px-4 py-2 text-[10px] font-semibold uppercase tracking-wider text-[var(--muted)] bg-[var(--surface)] w-36">{t('colCreated')}</th>
                <th className="px-4 py-2 bg-[var(--surface)] w-40" />
              </tr>
            </thead>
            <tbody>
              {boards.map(board => (
                <BoardRow key={board.id} board={board} />
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

function BoardRow({ board }: { board: Board }) {
  const { t } = useSettings();
  const color = boardColor(board.name);

  return (
    <tr
      className="border-b border-[var(--border)] transition-colors"
      onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = 'var(--surface)'; }}
      onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = ''; }}
    >
      <td className="px-8 py-3">
        <div className="flex items-center gap-3">
          <div className="w-3 h-3 rounded-sm shrink-0" style={{ background: color }} />
          <span className="text-sm font-medium text-[var(--foreground)]">{board.name}</span>
          {board.description && (
            <span className="text-xs text-[var(--muted)] truncate max-w-xs">{board.description}</span>
          )}
        </div>
      </td>
      <td className="px-4 py-3 text-xs text-[var(--muted)]">
        {new Date(board.createdAt).toLocaleDateString()}
      </td>
      <td className="px-4 py-3">
        <div className="flex items-center gap-2 justify-end">
          <Link
            href={`/boards/${board.id}`}
            className="px-3 py-1 rounded text-xs font-semibold text-[var(--primary)] border border-[var(--border)] hover:bg-[var(--primary-light)] transition-colors"
          >
            {t('openBoard')}
          </Link>
          <Link
            href={`/boards/${board.id}/canvas`}
            className="px-3 py-1 rounded text-xs font-semibold text-[var(--muted)] border border-[var(--border)] hover:bg-[var(--surface)] transition-colors"
          >
            {t('canvas')}
          </Link>
        </div>
      </td>
    </tr>
  );
}
