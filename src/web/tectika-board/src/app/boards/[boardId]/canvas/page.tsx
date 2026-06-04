'use client';

import { useParams } from 'next/navigation';
import Link from 'next/link';
import { BoardProvider, useBoard } from '@/lib/board-context';
import { CanvasView } from '@/components/board/canvas/CanvasView';
import { ItemPanel } from '@/components/workspace/ItemPanel';
import { Icon } from '@/components/ui/icons';
import { Spinner } from '@/components/ui/primitives';

function CanvasPageInner({ boardId }: { boardId: string }) {
  const { board, loading } = useBoard();
  return (
    <div className="flex flex-col h-full">
      <div className="px-5 py-3 border-b border-[var(--border)] flex items-center gap-3">
        <Link href={`/boards/${boardId}`} className="text-[var(--muted)] hover:text-[var(--foreground)]"><Icon.chevronLeft size={18} /></Link>
        <h1 className="text-lg font-bold text-[var(--foreground)]">{board?.name ?? 'Board'}</h1>
        <span className="text-xs text-[var(--muted)] bg-[var(--surface)] px-2 py-0.5 rounded-full">Flow Canvas</span>
      </div>
      <div className="flex-1 min-h-0 relative">
        {loading ? <div className="flex items-center justify-center h-full gap-2 text-[var(--muted)]"><Spinner /> Loading canvas…</div> : <CanvasView />}
        <ItemPanel />
      </div>
    </div>
  );
}

export default function CanvasPage() {
  const params = useParams();
  const boardId = params.boardId as string;
  return (
    <BoardProvider boardId={boardId}>
      <CanvasPageInner boardId={boardId} />
    </BoardProvider>
  );
}
