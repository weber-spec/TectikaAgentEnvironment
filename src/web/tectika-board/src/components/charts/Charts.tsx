'use client';

import React, { useState } from 'react';

export interface Datum { label: string; value: number; hex: string }

// ── Bar / Column chart ────────────────────────────────────────────────────────
export function BarChart({ data, horizontal, height = 240 }: { data: Datum[]; horizontal?: boolean; height?: number }) {
  const max = Math.max(1, ...data.map(d => d.value));
  if (horizontal) {
    return (
      <div className="flex flex-col gap-2 w-full" style={{ minHeight: height }}>
        {data.map(d => (
          <div key={d.label} className="flex items-center gap-2">
            <span className="text-xs text-[var(--muted)] w-28 truncate text-right shrink-0">{d.label}</span>
            <div className="flex-1 h-6 bg-[var(--surface)] rounded overflow-hidden">
              <div className="h-full rounded flex items-center justify-end px-2 text-[11px] font-semibold text-white transition-all" style={{ width: `${(d.value / max) * 100}%`, background: d.hex, minWidth: d.value ? 22 : 0 }}>{d.value || ''}</div>
            </div>
          </div>
        ))}
      </div>
    );
  }
  return (
    <div className="flex items-end gap-3 w-full px-2" style={{ height }}>
      {data.map(d => (
        <div key={d.label} className="flex-1 flex flex-col items-center justify-end gap-1.5 h-full">
          <span className="text-[11px] font-semibold text-[var(--foreground)]">{d.value}</span>
          <div className="w-full rounded-t-md transition-all hover:opacity-80" style={{ height: `${(d.value / max) * 100}%`, background: d.hex, minHeight: d.value ? 4 : 0 }} />
          <span className="text-[10px] text-[var(--muted)] truncate w-full text-center" title={d.label}>{d.label}</span>
        </div>
      ))}
    </div>
  );
}

// ── Pie / Donut chart ──────────────────────────────────────────────────────────
export function PieChart({ data, donut, size = 200 }: { data: Datum[]; donut?: boolean; size?: number }) {
  const total = data.reduce((s, d) => s + d.value, 0) || 1;
  const [hover, setHover] = useState<string | null>(null);
  const r = size / 2; const cx = r; const cy = r; const inner = donut ? r * 0.58 : 0;
  let angle = -Math.PI / 2;
  const arcs = data.filter(d => d.value > 0).map(d => {
    const frac = d.value / total;
    const a0 = angle; const a1 = angle + frac * Math.PI * 2; angle = a1;
    const large = a1 - a0 > Math.PI ? 1 : 0;
    const x0 = cx + r * Math.cos(a0), y0 = cy + r * Math.sin(a0);
    const x1 = cx + r * Math.cos(a1), y1 = cy + r * Math.sin(a1);
    const path = inner
      ? `M ${cx + inner * Math.cos(a0)} ${cy + inner * Math.sin(a0)} L ${x0} ${y0} A ${r} ${r} 0 ${large} 1 ${x1} ${y1} L ${cx + inner * Math.cos(a1)} ${cy + inner * Math.sin(a1)} A ${inner} ${inner} 0 ${large} 0 ${cx + inner * Math.cos(a0)} ${cy + inner * Math.sin(a0)} Z`
      : `M ${cx} ${cy} L ${x0} ${y0} A ${r} ${r} 0 ${large} 1 ${x1} ${y1} Z`;
    return { d, path, frac };
  });
  return (
    <div className="flex items-center gap-4 flex-wrap justify-center">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        {arcs.map(a => (
          <path key={a.d.label} d={a.path} fill={a.d.hex} stroke="var(--background)" strokeWidth={2}
            opacity={hover && hover !== a.d.label ? 0.4 : 1}
            onMouseEnter={() => setHover(a.d.label)} onMouseLeave={() => setHover(null)} style={{ transition: 'opacity .15s' }} />
        ))}
        {donut && <text x={cx} y={cy} textAnchor="middle" dominantBaseline="central" className="fill-[var(--foreground)]" style={{ fontSize: 22, fontWeight: 700 }}>{total}</text>}
      </svg>
      <div className="flex flex-col gap-1.5">
        {data.map(d => (
          <div key={d.label} className="flex items-center gap-2 text-xs" style={{ opacity: hover && hover !== d.label ? 0.4 : 1 }}>
            <span className="w-3 h-3 rounded-sm" style={{ background: d.hex }} />
            <span className="text-[var(--foreground)]">{d.label}</span>
            <span className="text-[var(--muted)]">{d.value} · {Math.round((d.value / total) * 100)}%</span>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Line chart ──────────────────────────────────────────────────────────────────
export function LineChart({ data, height = 220, color = '#0073ea' }: { data: Datum[]; height?: number; color?: string }) {
  const w = Math.max(320, data.length * 60);
  const max = Math.max(1, ...data.map(d => d.value));
  const pad = 28;
  const pts = data.map((d, i) => {
    const x = pad + (i / Math.max(1, data.length - 1)) * (w - pad * 2);
    const y = height - pad - (d.value / max) * (height - pad * 2);
    return { x, y, d };
  });
  const path = pts.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.x} ${p.y}`).join(' ');
  const area = `${path} L ${pts[pts.length - 1]?.x ?? pad} ${height - pad} L ${pad} ${height - pad} Z`;
  return (
    <svg width="100%" viewBox={`0 0 ${w} ${height}`} style={{ maxWidth: '100%' }}>
      <path d={area} fill={color} opacity={0.1} />
      <path d={path} fill="none" stroke={color} strokeWidth={2.5} strokeLinejoin="round" />
      {pts.map(p => (
        <g key={p.d.label}>
          <circle cx={p.x} cy={p.y} r={3.5} fill={color} />
          <text x={p.x} y={height - 8} textAnchor="middle" className="fill-[var(--muted)]" style={{ fontSize: 10 }}>{p.d.label}</text>
        </g>
      ))}
    </svg>
  );
}
