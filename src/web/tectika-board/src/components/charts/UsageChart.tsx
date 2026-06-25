'use client';

import React, { useMemo, useState } from 'react';
import type { UsageTimePoint } from '@/lib/types';
import { formatCompact, formatCurrency } from '@/lib/format';

type Granularity = 'day' | 'week' | 'month';

interface Bucket {
  key: string;
  label: string;       // short x-axis label
  rangeLabel: string;  // tooltip header
  tokens: number;
  costUsd: number;
  input: number;
  cachedInput: number;
  output: number;
  perModel: Record<string, { tokens: number; costUsd: number }>;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
// How many buckets to show per granularity (most recent N).
const LIMIT: Record<Granularity, number> = { day: 30, week: 16, month: 12 };

const parseUTC = (d: string) => new Date(`${d}T00:00:00Z`);
const mmdd = (d: Date) => `${MONTHS[d.getUTCMonth()]} ${d.getUTCDate()}`;

function mondayOf(d: Date): Date {
  const day = d.getUTCDay();                 // 0=Sun..6=Sat
  const shift = day === 0 ? -6 : 1 - day;    // back to Monday
  const r = new Date(d);
  r.setUTCDate(d.getUTCDate() + shift);
  return r;
}

function emptyBucket(key: string, label: string, rangeLabel: string): Bucket {
  return { key, label, rangeLabel, tokens: 0, costUsd: 0, input: 0, cachedInput: 0, output: 0, perModel: {} };
}

function add(b: Bucket, p: UsageTimePoint) {
  b.tokens += p.tokens; b.costUsd += p.costUsd;
  b.input += p.input ?? 0; b.cachedInput += p.cachedInput ?? 0; b.output += p.output ?? 0;
  for (const [model, v] of Object.entries(p.perModel ?? {})) {
    const m = (b.perModel[model] ??= { tokens: 0, costUsd: 0 });
    m.tokens += v.tokens; m.costUsd += v.costUsd;
  }
}

function bucketize(series: UsageTimePoint[], gran: Granularity): Bucket[] {
  if (gran === 'day') {
    return series.map(p => {
      const d = parseUTC(p.date);
      const b = emptyBucket(p.date, mmdd(d), d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' }));
      add(b, p);
      return b;
    });
  }
  const map = new Map<string, Bucket>();
  for (const p of series) {
    const d = parseUTC(p.date);
    let key: string, label: string, rangeLabel: string;
    if (gran === 'week') {
      const ws = mondayOf(d);
      const we = new Date(ws); we.setUTCDate(ws.getUTCDate() + 6);
      key = ws.toISOString().slice(0, 10);
      label = mmdd(ws);
      rangeLabel = `${mmdd(ws)} – ${mmdd(we)}`;
    } else {
      key = p.date.slice(0, 7);            // yyyy-MM
      label = MONTHS[d.getUTCMonth()];
      rangeLabel = `${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
    }
    let b = map.get(key);
    if (!b) { b = emptyBucket(key, label, rangeLabel); map.set(key, b); }
    add(b, p);
  }
  return [...map.values()].sort((a, b) => (a.key < b.key ? -1 : 1));
}

const GRANS: { id: Granularity; label: string }[] = [
  { id: 'day', label: 'Day' },
  { id: 'week', label: 'Week' },
  { id: 'month', label: 'Month' },
];

export function UsageChart({ series, color = '#0073ea', height = 240 }: { series: UsageTimePoint[]; color?: string; height?: number }) {
  const [gran, setGran] = useState<Granularity>('day');
  const [hover, setHover] = useState<number | null>(null);

  const buckets = useMemo(() => bucketize(series, gran).slice(-LIMIT[gran]), [series, gran]);
  const max = Math.max(1, ...buckets.map(b => b.tokens));
  const W = 1000, top = 16, bottom = height - 26;
  const N = buckets.length;
  const x = (i: number) => ((i + 0.5) / Math.max(1, N)) * W;
  const y = (v: number) => bottom - (v / max) * (bottom - top);

  const pts = buckets.map((b, i) => ({ x: x(i), y: y(b.tokens), b }));
  const line = pts.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' ');
  const area = N ? `${line} L ${x(N - 1).toFixed(1)} ${bottom} L ${x(0).toFixed(1)} ${bottom} Z` : '';
  const labelEvery = Math.max(1, Math.ceil(N / 8));
  const active = hover != null ? buckets[hover] : null;

  if (!buckets.some(b => b.tokens > 0)) {
    return (
      <div className="flex flex-col gap-3">
        <GranTabs gran={gran} setGran={setGran} />
        <p className="text-sm text-[var(--muted)]">No usage recorded in this period yet.</p>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <GranTabs gran={gran} setGran={setGran} />
      <div className="relative w-full" style={{ height }}>
        <svg width="100%" height="100%" viewBox={`0 0 ${W} ${height}`} preserveAspectRatio="none" style={{ display: 'block' }}>
          <path d={area} fill={color} opacity={0.1} />
          <path d={line} fill="none" stroke={color} strokeWidth={2.5} strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
          {active && hover != null && (
            <line x1={x(hover)} y1={top} x2={x(hover)} y2={bottom} stroke={color} strokeWidth={1} strokeDasharray="4 3" opacity={0.5} vectorEffect="non-scaling-stroke" />
          )}
          {pts.map((p, i) => (
            <circle key={p.b.key} cx={p.x} cy={p.y} r={hover === i ? 5 : 3} fill={color} vectorEffect="non-scaling-stroke" />
          ))}
        </svg>

        {/* X-axis labels (HTML, so they don't stretch with preserveAspectRatio=none) */}
        <div className="absolute inset-x-0 bottom-0 flex pointer-events-none">
          {buckets.map((b, i) => (
            <span key={b.key} className="flex-1 text-center text-[10px] text-[var(--muted)] truncate">
              {i % labelEvery === 0 ? b.label : ''}
            </span>
          ))}
        </div>

        {/* Hover capture cells, one per bucket */}
        <div className="absolute inset-0 flex">
          {buckets.map((b, i) => (
            <div key={b.key} className="flex-1 h-full" onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(h => (h === i ? null : h))} />
          ))}
        </div>

        {/* Tooltip */}
        {active && hover != null && (
          <div
            className="absolute z-10 pointer-events-none bg-[var(--background)] border border-[var(--border)] rounded-lg shadow-lg p-2.5 text-xs min-w-[170px]"
            style={{ left: `${((hover + 0.5) / N) * 100}%`, top: 0, transform: `translateX(${hover < N / 2 ? '8px' : 'calc(-100% - 8px)'})` }}
          >
            <div className="font-semibold text-[var(--foreground)] mb-1.5">{active.rangeLabel}</div>
            <Row label="Total" value={formatCompact(active.tokens)} strong />
            <Row label="Input" value={formatCompact(active.input)} />
            <Row label="Cached" value={formatCompact(active.cachedInput)} />
            <Row label="Output" value={formatCompact(active.output)} />
            <Row label="Cost" value={formatCurrency(active.costUsd)} />
            {Object.keys(active.perModel).length > 0 && (
              <div className="mt-1.5 pt-1.5 border-t border-[var(--border)] flex flex-col gap-0.5">
                {Object.entries(active.perModel).sort(([, a], [, b2]) => b2.tokens - a.tokens).slice(0, 3).map(([model, v]) => (
                  <Row key={model} label={model.split('/').pop() ?? model} value={formatCompact(v.tokens)} muted />
                ))}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function GranTabs({ gran, setGran }: { gran: Granularity; setGran: (g: Granularity) => void }) {
  return (
    <div className="flex items-center gap-1 self-end bg-[var(--surface)] rounded-lg p-0.5">
      {GRANS.map(g => (
        <button
          key={g.id}
          onClick={() => setGran(g.id)}
          className={`px-2.5 py-1 text-xs font-medium rounded-md transition-colors ${gran === g.id ? 'bg-[var(--background)] text-[var(--foreground)] shadow-sm' : 'text-[var(--muted)] hover:text-[var(--foreground)]'}`}
        >
          {g.label}
        </button>
      ))}
    </div>
  );
}

function Row({ label, value, strong, muted }: { label: string; value: string; strong?: boolean; muted?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <span className={muted ? 'text-[var(--muted)] truncate' : 'text-[var(--muted)]'}>{label}</span>
      <span className={`font-mono ${strong ? 'font-bold text-[var(--foreground)]' : 'text-[var(--foreground)]'}`}>{value}</span>
    </div>
  );
}
