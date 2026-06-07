'use client';

import React from 'react';
import type { Person } from '@/lib/types';
import { initials } from '@/lib/format';
import { textOn } from '@/lib/palette';

// ── Avatar ──────────────────────────────────────────────────────────────────

export function Avatar({ person, name, hex, size = 26, title, ring }: {
  person?: Person; name?: string; hex?: string; size?: number; title?: string; ring?: boolean;
}) {
  const label = person?.name ?? name ?? '?';
  const bg = person?.hex ?? hex ?? '#676879';
  const isAgent = person?.kind === 'Agent';
  return (
    <div
      className="rounded-full flex items-center justify-center font-bold shrink-0 select-none"
      style={{
        width: size, height: size, background: bg, color: textOn(bg),
        fontSize: size * 0.4,
        boxShadow: ring ? `0 0 0 2px var(--background), 0 0 0 3px ${bg}55` : undefined,
      }}
      title={title ?? label}
    >
      {isAgent ? '⚡' : initials(label)}
    </div>
  );
}

export function AvatarStack({ people, size = 24, max = 3 }: { people: Person[]; size?: number; max?: number }) {
  const shown = people.slice(0, max);
  const extra = people.length - shown.length;
  return (
    <div className="flex items-center" style={{ paddingLeft: 2 }}>
      {shown.map((p, i) => (
        <div key={p.id} style={{ marginLeft: i === 0 ? 0 : -8, zIndex: shown.length - i }}>
          <Avatar person={p} size={size} ring />
        </div>
      ))}
      {extra > 0 && (
        <div className="rounded-full flex items-center justify-center font-semibold text-[var(--muted)] bg-[var(--surface)] border border-[var(--border)]"
          style={{ width: size, height: size, fontSize: size * 0.38, marginLeft: -8, zIndex: 0 }}>
          +{extra}
        </div>
      )}
    </div>
  );
}

// ── Pill ──────────────────────────────────────────────────────────────────────

export function Pill({ label, hex, full, onClick, dropdown }: {
  label: string; hex: string; full?: boolean; onClick?: (e: React.MouseEvent) => void; dropdown?: boolean;
}) {
  return (
    <button
      onClick={onClick}
      disabled={!onClick}
      className="inline-flex items-center justify-center font-semibold transition-transform active:scale-[0.98]"
      style={{
        background: hex, color: textOn(hex),
        borderRadius: full ? 4 : 12,
        padding: full ? '0' : '2px 10px',
        width: full ? '100%' : undefined,
        height: full ? '100%' : undefined,
        minHeight: full ? 36 : 22,
        fontSize: 12.5,
        cursor: onClick ? 'pointer' : 'default',
        gap: 4,
      }}
    >
      <span className="truncate">{label}</span>
      {dropdown && onClick && (
        <svg width="9" height="9" viewBox="0 0 24 24" fill="none" className="opacity-60 shrink-0">
          <path d="M6 9l6 6 6-6" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      )}
    </button>
  );
}

// ── Button ────────────────────────────────────────────────────────────────────

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export function Button({ variant = 'secondary', size = 'md', children, className = '', ...rest }:
  React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: 'sm' | 'md' }) {
  const base = 'inline-flex items-center gap-1.5 font-semibold rounded-md transition-all disabled:opacity-50 disabled:cursor-not-allowed whitespace-nowrap';
  const sizes = size === 'sm' ? 'text-xs px-2.5 py-1.5' : 'text-sm px-4 py-2';
  const variants: Record<ButtonVariant, string> = {
    primary: 'text-white hover:brightness-110 shadow-sm',
    secondary: 'text-[var(--foreground)] border border-[var(--border)] hover:bg-[var(--surface)]',
    ghost: 'text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]',
    danger: 'text-white bg-[#e2445c] hover:brightness-110',
  };
  const style = variant === 'primary' ? { background: 'var(--primary)' } : undefined;
  return <button className={`${base} ${sizes} ${variants[variant]} ${className}`} style={style} {...rest}>{children}</button>;
}

export function IconButton({ children, active, className = '', label, ...rest }:
  React.ButtonHTMLAttributes<HTMLButtonElement> & { active?: boolean; label?: string }) {
  return (
    <button
      aria-label={label}
      title={label}
      className={`inline-flex items-center justify-center w-8 h-8 rounded-md transition-colors ${active ? 'bg-[var(--primary-light)] text-[var(--primary)]' : 'text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--foreground)]'} ${className}`}
      {...rest}
    >
      {children}
    </button>
  );
}

// ── Spinner / Skeleton ──────────────────────────────────────────────────────

export function Spinner({ size = 16 }: { size?: number }) {
  return <div className="border-2 border-[var(--primary)] border-t-transparent rounded-full animate-spin" style={{ width: size, height: size }} />;
}

export function Skeleton({ className = '', style }: { className?: string; style?: React.CSSProperties }) {
  return <div className={`shimmer rounded ${className}`} style={style} />;
}

export function SkeletonRows({ rows = 6 }: { rows?: number }) {
  return (
    <div className="flex flex-col gap-2 p-6">
      {Array.from({ length: rows }).map((_, i) => (
        <Skeleton key={i} className="h-10 w-full" style={{ opacity: 1 - i * 0.08 }} />
      ))}
    </div>
  );
}

// ── EmptyState ──────────────────────────────────────────────────────────────

export function EmptyState({ icon, title, description, action }: {
  icon?: React.ReactNode; title: string; description?: string; action?: React.ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center py-20 px-6 text-center">
      {icon && <div className="text-[var(--muted-2)] mb-4">{icon}</div>}
      <p className="text-base font-semibold text-[var(--foreground)] mb-1">{title}</p>
      {description && <p className="text-sm text-[var(--muted)] mb-5 max-w-sm">{description}</p>}
      {action}
    </div>
  );
}

// ── Toggle ────────────────────────────────────────────────────────────────────

export function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label?: string }) {
  return (
    <button
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
      className="relative inline-flex items-center rounded-full transition-colors shrink-0"
      style={{ width: 38, height: 22, background: checked ? 'var(--primary)' : '#c3c6d4' }}
    >
      <span className="absolute bg-white rounded-full shadow transition-transform"
        style={{ width: 16, height: 16, left: 3, transform: checked ? 'translateX(16px)' : 'translateX(0)' }} />
    </button>
  );
}

// ── Tooltip (CSS-only, hover) ─────────────────────────────────────────────────

export function Tooltip({ text, children }: { text: string; children: React.ReactNode }) {
  return (
    <span className="relative group/tip inline-flex">
      {children}
      <span className="pointer-events-none absolute left-1/2 -translate-x-1/2 bottom-[calc(100%+6px)] z-[100] whitespace-nowrap bg-[#1e2235] text-white text-[11px] font-medium px-2 py-1 rounded-md shadow-lg opacity-0 group-hover/tip:opacity-100 transition-opacity">
        {text}
      </span>
    </span>
  );
}

// ── Tags ────────────────────────────────────────────────────────────────────

export function Tag({ label, hex }: { label: string; hex: string }) {
  return (
    <span className="inline-flex items-center text-[11px] font-medium rounded px-1.5 py-0.5"
      style={{ background: `${hex}22`, color: hex }}>
      {label}
    </span>
  );
}
