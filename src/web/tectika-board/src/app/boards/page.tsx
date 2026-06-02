'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { api } from '@/lib/api';
import type { Board } from '@/lib/types';

export default function BoardsPage() {
  const [boards, setBoards] = useState<Board[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.boards.list()
      .then(setBoards)
      .catch(() => setBoards([]))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="max-w-screen-xl mx-auto px-6 py-10 w-full">
      {/* Page header */}
      <div className="flex items-center justify-between mb-8">
        <div>
          <h1 className="text-2xl font-bold gradient-text mb-1" style={{ fontFamily: 'var(--font-sora), sans-serif' }}>
            Boards
          </h1>
          <p className="text-sm text-[#8892aa]">Manage your AI agent workspaces</p>
        </div>
        <button
          className="flex items-center gap-2 px-4 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-500 text-white text-sm font-medium transition-colors glow-primary"
          onClick={async () => {
            const name = prompt('Board name?');
            if (!name) return;
            const board = await api.boards.create(name).catch(() => null);
            if (board) setBoards(prev => [...prev, board]);
          }}
        >
          <span className="text-lg leading-none">+</span>
          New Board
        </button>
      </div>

      {/* Board grid */}
      {loading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {[...Array(3)].map((_, i) => (
            <div key={i} className="h-36 rounded-2xl bg-[#1a1f2e] animate-pulse border border-[#2d3651]" />
          ))}
        </div>
      ) : boards.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-24 text-[#8892aa]">
          <span className="text-5xl mb-4">🤖</span>
          <p className="text-lg font-medium text-[#e8ecf4] mb-1">No boards yet</p>
          <p className="text-sm">Create your first board to start assigning tasks to AI agents</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {boards.map(board => (
            <BoardCard key={board.id} board={board} />
          ))}
        </div>
      )}
    </div>
  );
}

function BoardCard({ board }: { board: Board }) {
  return (
    <div className="rounded-2xl border border-[#2d3651] bg-[#1a1f2e] p-5 hover:border-indigo-500/50 transition-all hover:shadow-lg hover:shadow-indigo-500/5 group">
      <div className="flex items-start justify-between mb-3">
        <div className="w-9 h-9 rounded-xl bg-gradient-to-br from-indigo-500 to-cyan-500 flex items-center justify-center text-white font-bold text-sm shrink-0">
          {board.name.charAt(0).toUpperCase()}
        </div>
        <span className="text-xs text-[#8892aa]">
          {new Date(board.createdAt).toLocaleDateString()}
        </span>
      </div>

      <h2 className="font-semibold text-[#e8ecf4] mb-1 group-hover:text-indigo-300 transition-colors" style={{ fontFamily: 'var(--font-sora), sans-serif' }}>
        {board.name}
      </h2>
      {board.description && (
        <p className="text-xs text-[#8892aa] mb-4 line-clamp-2">{board.description}</p>
      )}

      <div className="flex gap-2 mt-4">
        <Link
          href={`/boards/${board.id}`}
          className="flex-1 text-center text-xs py-1.5 rounded-lg bg-[#232a3b] text-[#e8ecf4] hover:bg-[#2d3651] transition-colors border border-[#2d3651]"
        >
          📋 Task Board
        </Link>
        <Link
          href={`/canvas/${board.id}`}
          className="flex-1 text-center text-xs py-1.5 rounded-lg bg-indigo-600/20 text-indigo-300 hover:bg-indigo-600/30 transition-colors border border-indigo-500/30"
        >
          🔗 Flow Canvas
        </Link>
      </div>
    </div>
  );
}
