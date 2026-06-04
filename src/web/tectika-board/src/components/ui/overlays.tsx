'use client';

import React, { useEffect, useLayoutEffect, useRef, useState, useCallback } from 'react';
import { createPortal } from 'react-dom';

// ── Popover ────────────────────────────────────────────────────────────────
// Anchored floating layer. Closes on outside-click and Escape. Auto-flips.

type Align = 'start' | 'end' | 'center';
export function Popover({ anchorRef, open, onClose, children, align = 'start', width, className = '' }: {
  anchorRef: React.RefObject<HTMLElement | null>;
  open: boolean;
  onClose: () => void;
  children: React.ReactNode;
  align?: Align;
  width?: number;
  className?: string;
}) {
  const layerRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);

  const place = useCallback(() => {
    const a = anchorRef.current; const l = layerRef.current;
    if (!a || !l) return;
    const ar = a.getBoundingClientRect();
    const lw = width ?? l.offsetWidth;
    const lh = l.offsetHeight;
    let left = align === 'end' ? ar.right - lw : align === 'center' ? ar.left + ar.width / 2 - lw / 2 : ar.left;
    let top = ar.bottom + 6;
    if (left + lw > window.innerWidth - 8) left = window.innerWidth - lw - 8;
    if (left < 8) left = 8;
    if (top + lh > window.innerHeight - 8) top = Math.max(8, ar.top - lh - 6);
    setPos({ top, left });
  }, [anchorRef, align, width]);

  useLayoutEffect(() => { if (open) place(); }, [open, place]);
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (layerRef.current?.contains(e.target as Node)) return;
      if (anchorRef.current?.contains(e.target as Node)) return;
      onClose();
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    const onScroll = () => place();
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    window.addEventListener('resize', onScroll);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('resize', onScroll);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [open, onClose, anchorRef, place]);

  if (!open || !mounted) return null;
  return createPortal(
    <div
      ref={layerRef}
      className={`fixed z-[1000] bg-[var(--background)] rounded-lg shadow-2xl border border-[var(--border)] animate-scale-in ${className}`}
      style={{ top: pos?.top ?? -9999, left: pos?.left ?? -9999, width, visibility: pos ? 'visible' : 'hidden' }}
    >
      {children}
    </div>,
    document.body,
  );
}

// ── Menu (list dropdown) ──────────────────────────────────────────────────────

export interface MenuOption {
  label: string;
  icon?: React.ReactNode;
  onClick?: () => void;
  danger?: boolean;
  checked?: boolean;
  disabled?: boolean;
  hex?: string;
}

export function Menu({ anchorRef, open, onClose, options, align, width = 200, header }: {
  anchorRef: React.RefObject<HTMLElement | null>;
  open: boolean; onClose: () => void; options: (MenuOption | 'divider')[];
  align?: Align; width?: number; header?: React.ReactNode;
}) {
  return (
    <Popover anchorRef={anchorRef} open={open} onClose={onClose} align={align} width={width} className="py-1">
      {header}
      {options.map((opt, i) =>
        opt === 'divider' ? (
          <div key={i} className="my-1 border-t border-[var(--border)]" />
        ) : (
          <button
            key={i}
            disabled={opt.disabled}
            onClick={() => { opt.onClick?.(); onClose(); }}
            className={`w-full flex items-center gap-2.5 px-3 py-1.5 text-left text-[13px] transition-colors disabled:opacity-40 ${opt.danger ? 'text-[#e2445c] hover:bg-[#e2445c11]' : 'text-[var(--foreground)] hover:bg-[var(--surface)]'}`}
          >
            {opt.hex && <span className="w-3 h-3 rounded-sm shrink-0" style={{ background: opt.hex }} />}
            {opt.icon && <span className="shrink-0 text-[var(--muted)]">{opt.icon}</span>}
            <span className="flex-1 truncate">{opt.label}</span>
            {opt.checked && <span className="text-[var(--primary)]">✓</span>}
          </button>
        ),
      )}
    </Popover>
  );
}

// ── Modal ─────────────────────────────────────────────────────────────────────

export function Modal({ open, onClose, children, title, width = 560, footer }: {
  open: boolean; onClose: () => void; children: React.ReactNode; title?: React.ReactNode; width?: number; footer?: React.ReactNode;
}) {
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open || !mounted) return null;
  return createPortal(
    <div className="fixed inset-0 z-[1100] flex items-start justify-center p-4 sm:p-8 overflow-auto"
      style={{ background: 'rgba(0,0,0,0.45)' }} onMouseDown={onClose}>
      <div
        className="bg-[var(--background)] rounded-xl shadow-2xl border border-[var(--border)] my-auto animate-scale-in w-full"
        style={{ maxWidth: width }}
        onMouseDown={e => e.stopPropagation()}
      >
        {title && (
          <div className="flex items-center justify-between px-5 py-3.5 border-b border-[var(--border)]">
            <h2 className="text-base font-semibold text-[var(--foreground)]">{title}</h2>
            <button onClick={onClose} className="text-[var(--muted)] hover:text-[var(--foreground)] w-7 h-7 flex items-center justify-center rounded-md hover:bg-[var(--surface)]">✕</button>
          </div>
        )}
        <div className="px-5 py-4">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 px-5 py-3.5 border-t border-[var(--border)]">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
}
