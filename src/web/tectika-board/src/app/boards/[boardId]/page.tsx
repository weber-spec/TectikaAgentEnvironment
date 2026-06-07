'use client';

import { useParams } from 'next/navigation';
import { BoardProvider } from '@/lib/board-context';
import { BoardView } from '@/components/board/BoardView';

export default function BoardPage() {
  const params = useParams();
  const boardId = params.boardId as string;
  return (
    <BoardProvider boardId={boardId}>
      <BoardView />
    </BoardProvider>
  );
}
