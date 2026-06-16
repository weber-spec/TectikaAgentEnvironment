'use client';
import React from 'react';
import { Icon } from '@/components/ui/icons';
import type { ChatCommand, ChatCommandContext } from '@/lib/chat-commands';

/** Inline command palette floating above the chat input. Pure render — the parent (ChatTab) owns the
/// query, the highlighted index, and keyboard handling (so the textarea keeps focus). */
export function CommandMenu({ items, active, ctx, onHover, onPick }: {
  items: ChatCommand[];
  active: number;
  ctx: ChatCommandContext;
  onHover: (i: number) => void;
  onPick: (c: ChatCommand) => void;
}) {
  return (
    <div className="absolute bottom-full left-0 right-0 mb-1 z-20 rounded-lg border border-[var(--border)] bg-[var(--background)] shadow-xl overflow-hidden max-h-[40vh] overflow-y-auto">
      {items.length === 0 && (
        <div className="px-3 py-3 text-xs text-[var(--muted)]">No matching commands</div>
      )}
      {items.map((c, i) => {
        const ok = c.enabled(ctx);
        const I = Icon[c.icon];
        return (
          <button key={c.name} disabled={!ok} type="button"
            onMouseEnter={() => onHover(i)}
            onClick={() => onPick(c)}
            className={`w-full flex items-center gap-2.5 px-3 py-2 text-left ${i === active ? 'bg-[var(--primary-light)]' : ''} ${ok ? '' : 'opacity-40 cursor-not-allowed'}`}>
            <I size={14} className="text-[var(--muted)] shrink-0" />
            <span className="text-[13px] text-[var(--foreground)] font-medium">/{c.name}</span>
            <span className="text-[11px] text-[var(--muted)] truncate">{c.description}</span>
          </button>
        );
      })}
    </div>
  );
}
