'use client';

import { useEffect, useState } from 'react';
import { useParams } from 'next/navigation';
import Link from 'next/link';
import { api } from '@/lib/api';
import { TaskRow } from '@/components/board/TaskRow';
import type { AgentTask, AgentRole, Board } from '@/lib/types';

export default function BoardPage() {
  const params = useParams();
  const boardId = params.boardId as string;

  const [board, setBoard] = useState<Board | null>(null);
  const [tasks, setTasks] = useState<AgentTask[]>([]);
  const [roles, setRoles] = useState<AgentRole[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!boardId) return;
    Promise.all([
      api.boards.list().then(bs => bs.find(b => b.id === boardId) ?? null),
      api.tasks.list(boardId),
      api.agentRoles.list(),
    ])
      .then(([b, t, r]) => {
        setBoard(b);
        setTasks(t);
        setRoles(r);
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, [boardId]);

  const handleAddTask = async () => {
    const title = prompt('Task title?');
    if (!title) return;
    const task = await api.tasks.create(boardId, { title }).catch(() => null);
    if (task) setTasks(prev => [...prev, task]);
  };

  return (
    <div className="flex flex-col h-full">
      {/* Board header */}
      <div className="bg-white border-b border-[#e6e9ef] px-8 py-3 flex items-center gap-4">
        <Link href="/boards" className="text-[#676879] hover:text-[#323338] transition-colors">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
            <path d="M19 12H5M12 19l-7-7 7-7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        </Link>
        <h1 className="text-lg font-semibold text-[#323338]">
          {loading ? '...' : (board?.name ?? 'Board')}
        </h1>
        <div className="flex-1" />
        <Link
          href={`/canvas/${boardId}`}
          className="px-3 py-1.5 rounded text-xs font-semibold text-[#676879] border border-[#e6e9ef] hover:bg-[#f5f6f8] transition-colors"
        >
          Flow Canvas
        </Link>
        <button
          className="flex items-center gap-1.5 px-4 py-1.5 rounded text-sm font-semibold text-white transition-colors"
          style={{ background: '#0073ea' }}
          onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = '#1f76c2'; }}
          onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = '#0073ea'; }}
          onClick={handleAddTask}
        >
          <span className="text-base font-bold leading-none">+</span>
          Add task
        </button>
      </div>

      {/* Board table */}
      <div className="flex-1 overflow-auto">
        {loading ? (
          <div className="px-8 py-6 flex flex-col gap-2">
            {[...Array(5)].map((_, i) => (
              <div key={i} className="h-10 rounded bg-[#f5f6f8] animate-pulse" />
            ))}
          </div>
        ) : (
          <table className="w-full border-collapse min-w-[700px]">
            <thead>
              <tr className="border-b border-[#e6e9ef] sticky top-0 z-10">
                <th className="w-8 pl-3 pr-1 py-2 bg-[#f5f6f8]" />
                <th className="text-left px-3 py-2 text-[10px] font-semibold uppercase tracking-wider text-[#676879] bg-[#f5f6f8] min-w-[220px]">
                  Task
                </th>
                <th className="text-left px-3 py-2 text-[10px] font-semibold uppercase tracking-wider text-[#676879] bg-[#f5f6f8] w-36">
                  Assignee
                </th>
                <th className="text-left px-3 py-2 text-[10px] font-semibold uppercase tracking-wider text-[#676879] bg-[#f5f6f8] w-36">
                  Status
                </th>
                <th className="text-left px-3 py-2 text-[10px] font-semibold uppercase tracking-wider text-[#676879] bg-[#f5f6f8] w-28">
                  Priority
                </th>
                <th className="text-left px-3 py-2 text-[10px] font-semibold uppercase tracking-wider text-[#676879] bg-[#f5f6f8] w-28">
                  Deps
                </th>
              </tr>
            </thead>
            <tbody>
              {tasks.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-8 py-16 text-center text-sm text-[#676879]">
                    No tasks yet.{' '}
                    <button
                      className="text-[#0073ea] font-semibold hover:underline"
                      onClick={handleAddTask}
                    >
                      + Add a task
                    </button>
                  </td>
                </tr>
              ) : (
                tasks.map(task => (
                  <TaskRow key={task.id} task={task} roles={roles} />
                ))
              )}
            </tbody>
          </table>
        )}

        {/* Group footer — add task inline */}
        {!loading && tasks.length > 0 && (
          <button
            className="flex items-center gap-2 px-4 py-2 text-sm text-[#676879] hover:text-[#323338] hover:bg-[#f5f6f8] transition-colors w-full text-left border-t border-[#e6e9ef]"
            onClick={handleAddTask}
          >
            <span className="text-base font-bold text-[#0073ea]">+</span>
            Add task
          </button>
        )}
      </div>
    </div>
  );
}
