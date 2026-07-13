'use client';

import { useId } from 'react';

const BRAND_FROM = '#0073ea';
const BRAND_TO = '#00c875';
const GRADIENT = `linear-gradient(135deg, ${BRAND_FROM}, ${BRAND_TO})`;

/**
 * The chains.team mark: two arcs orbiting a solid core — a chain caught mid-turn.
 * Drawn as an open stroke with no container, so it sits directly on the navbar.
 */
export function Logomark({ size = 26 }: { size?: number }) {
  const gid = useId();
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden className="shrink-0">
      <defs>
        <linearGradient id={gid} x1="5" y1="18" x2="19" y2="6" gradientUnits="userSpaceOnUse">
          <stop stopColor={BRAND_FROM} />
          <stop offset="1" stopColor={BRAND_TO} />
        </linearGradient>
      </defs>
      <path d="M9.4 4.6A5.6 5.6 0 0 0 6.4 15" stroke="var(--foreground)" strokeWidth="1.9" strokeLinecap="round" />
      <path d="M14.6 19.4A5.6 5.6 0 0 0 17.6 9" stroke={`url(#${gid})`} strokeWidth="1.9" strokeLinecap="round" />
      <circle cx="12" cy="12" r="2.6" fill={`url(#${gid})`} />
    </svg>
  );
}

/**
 * chains.team — the "ai" inside "ch-ai-ns" carries the brand gradient.
 * `size` drives everything so the mark stays proportional wherever it lands.
 */
export function Wordmark({ size = 14, showSuffix = true }: { size?: number; showSuffix?: boolean }) {
  return (
    <span
      className="font-semibold text-[var(--foreground)] tracking-tight whitespace-nowrap select-none"
      style={{ fontSize: size }}
      aria-label="chains.team"
    >
      <span aria-hidden>ch</span>
      <span
        aria-hidden
        className="font-bold"
        style={{
          background: GRADIENT,
          WebkitBackgroundClip: 'text',
          backgroundClip: 'text',
          color: 'transparent',
          // the gradient letters read slightly narrow against the plain ones
          letterSpacing: '0.01em',
        }}
      >
        ai
      </span>
      <span aria-hidden>ns</span>
      {showSuffix && (
        <span aria-hidden className="font-normal text-[var(--muted)]" style={{ fontSize: size * 0.85 }}>
          .team
        </span>
      )}
    </span>
  );
}
