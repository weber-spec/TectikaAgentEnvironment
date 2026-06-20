'use client';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { UsageRollup } from '@/lib/types';
import { formatCurrency, formatCompact } from '@/lib/format';

export function UsagePanel({ taskId }: { taskId: string }) {
  const [rollup, setRollup] = useState<UsageRollup | null>(null);
  const [view, setView] = useState<'session' | 'lifetime'>('session');

  useEffect(() => {
    let on = true;
    api.usage.task(taskId).then(r => { if (on) setRollup(r); }).catch(() => {});
    return () => { on = false; };
  }, [taskId]);

  if (!rollup) return null;

  const sessionBucket = rollup.currentSession ?? undefined;
  const bucket = view === 'session' ? sessionBucket : rollup.lifetime;
  const noSession = view === 'session' && !sessionBucket;
  const models = Object.entries(rollup.perModel);

  return (
    <div className="rounded-lg border border-[var(--border)] p-3 bg-[var(--background)]">
      <div className="flex items-center justify-between mb-2">
        <span className="font-semibold text-[var(--foreground)]">Usage</span>
        <div className="inline-flex rounded-md border border-[var(--border)] text-xs overflow-hidden" role="tablist" aria-label="Usage scope">
          {(['session', 'lifetime'] as const).map(v => (
            <button key={v} role="tab" aria-selected={view === v} type="button"
              className={`px-2 py-1 ${view === v ? 'bg-[var(--accent)] text-white' : 'text-[var(--muted)]'}`}
              onClick={() => setView(v)}>{v === 'session' ? 'This session' : 'Task lifetime'}</button>
          ))}
        </div>
      </div>
      {noSession
        ? <p className="text-xs text-[var(--muted)] text-center py-2 mb-3">No usage in the current session yet.</p>
        : <div className="grid grid-cols-2 gap-2 text-center mb-3">
            <div><div className="text-[var(--muted)] text-xs">Tokens</div><div className="font-semibold">{formatCompact(bucket?.tokens.total)}</div></div>
            <div><div className="text-[var(--muted)] text-xs">Cost</div><div className="font-semibold">{formatCurrency(bucket?.costUsd)}</div></div>
          </div>
      }
      <p className="text-[10px] text-[var(--muted)] uppercase tracking-wide font-semibold mb-1">By model (lifetime)</p>
      <table className="w-full text-xs">
        <thead><tr className="text-[var(--muted)] text-left"><th>Model</th><th className="text-right">Tokens</th><th className="text-right">Cost</th></tr></thead>
        <tbody>
          {models.map(([model, b]) => (
            <tr key={model}><td>{model}</td><td className="text-right">{formatCompact(b.tokens.total)}</td><td className="text-right">{formatCurrency(b.costUsd)}</td></tr>
          ))}
          {models.length === 0 && (<tr><td colSpan={3} className="text-[var(--muted)] py-2">No usage yet</td></tr>)}
        </tbody>
      </table>
    </div>
  );
}
