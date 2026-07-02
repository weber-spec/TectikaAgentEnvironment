'use client';

// Brand icons for the Connections catalog. Real logos come from `simple-icons` (official path + hex);
// brands not in simple-icons (Slack, Azure — excluded there by brand policy) render a brand-colored
// monogram tile. `name` is the catalog entry's iconKey.

import type { CSSProperties } from 'react';
import { siGithub, siClaude, siGmail, siResend, siGoogle } from 'simple-icons';

type SimpleIcon = { title: string; hex: string; path: string };

/** iconKey → official simple-icons logo. */
const LOGOS: Record<string, SimpleIcon> = {
  github: siGithub,
  anthropic: siClaude,   // the "Claude (Anthropic)" model provider — Claude is the recognizable mark
  claude: siClaude,
  gmail: siGmail,
  resend: siResend,
  email: siResend,       // Email integration is powered by Resend
  google: siGoogle,
};

/** Monogram fallback for brands with no simple-icons logo (color + short label). */
const MONOGRAMS: Record<string, { label: string; hex: string }> = {
  slack:   { label: 'Sl', hex: '#4A154B' },
  foundry: { label: 'AI', hex: '#0078D4' },  // Azure AI Foundry
  azure:   { label: 'Az', hex: '#0078D4' },
};
const DEFAULT_MONO = { label: '⚡', hex: '#0073ea' };

export function BrandIcon({ name, size = 28, className }: { name: string; size?: number; className?: string }) {
  const key = (name || '').toLowerCase();
  const logo = LOGOS[key];
  const hex = logo ? `#${logo.hex}` : (MONOGRAMS[key] ?? DEFAULT_MONO).hex;

  const tile: CSSProperties = {
    width: size, height: size,
    background: `${hex}1a`,   // 10% tint
    color: hex,
    borderRadius: Math.max(6, size * 0.22),
  };

  return (
    <span className={`inline-flex items-center justify-center shrink-0 font-bold ${className ?? ''}`} style={tile} aria-hidden="true">
      {logo ? (
        <svg width={size * 0.56} height={size * 0.56} viewBox="0 0 24 24" fill="currentColor">
          <path d={logo.path} />
        </svg>
      ) : (
        <span style={{ fontSize: size * 0.42, lineHeight: 1 }}>{(MONOGRAMS[key] ?? DEFAULT_MONO).label}</span>
      )}
    </span>
  );
}
