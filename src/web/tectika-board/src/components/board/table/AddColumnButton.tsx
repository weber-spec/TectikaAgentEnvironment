'use client';

import React, { useRef, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import { KIND_META } from '@/lib/columns';
import type { ColumnKind } from '@/lib/types';
import { Popover } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';

// The column kinds offered in the "add column" menu (excludes built-in singletons).
const ADDABLE: ColumnKind[] = [
  'text', 'number', 'status', 'dropdown', 'people', 'date', 'timeline',
  'tags', 'progress', 'rating', 'checkbox', 'link', 'priority', 'formula',
  'tokens', 'cost', 'trigger', 'lastUpdated', 'itemId', 'autoNumber',
];

export function AddColumnButton() {
  const { addColumn } = useBoard();
  const ref = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  return (
    <>
      <button ref={ref} onClick={() => setOpen(o => !o)} title="Add column"
        className="w-7 h-7 flex items-center justify-center rounded-md text-[var(--muted)] hover:bg-[var(--background)] hover:text-[var(--primary)] border border-dashed border-[var(--border)]">
        <Icon.plus size={15} />
      </button>
      <Popover anchorRef={ref} open={open} onClose={() => setOpen(false)} width={260} className="p-2">
        <div className="px-2 py-1 text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Add a column</div>
        <div className="grid grid-cols-2 gap-1 max-h-[320px] overflow-auto">
          {ADDABLE.map(kind => (
            <button key={kind} onClick={() => { addColumn(kind); setOpen(false); }}
              className="flex items-center gap-2 px-2 py-2 rounded-md hover:bg-[var(--surface)] text-left">
              <span className="w-7 h-7 rounded-md flex items-center justify-center bg-[var(--surface)] text-[var(--muted)] shrink-0"><Icon.plus size={13} /></span>
              <span className="text-[12px] text-[var(--foreground)] truncate">{KIND_META[kind].label}</span>
            </button>
          ))}
        </div>
      </Popover>
    </>
  );
}
