'use client';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { UsageRollup, UsageBucket } from '@/lib/types';

function fmtCost(c: number) { return `$${c.toFixed(2)}`; }
function fmtTokens(n: number) { return n.toLocaleString(); }

export function UsagePanel({ taskId }: { taskId: string }) {
  const [rollup, setRollup] = useState<UsageRollup | null>(null);
  const [view, setView] = useState<'session' | 'lifetime'>('session');

  useEffect(() => {
    let on = true;
    api.usage.task(taskId).then(r => { if (on) setRollup(r); }).catch(() => {});
    return () => { on = false; };
  }, [taskId]);

  if (!rollup) return null;
  const bucket: UsageBucket | undefined = view === 'session' ? (rollup.currentSession ?? undefined) : rollup.lifetime;
  const tokens = bucket?.tokens.total ?? 0;
  const cost = bucket?.costUsd ?? 0;
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
      <div className="grid grid-cols-2 gap-2 text-center mb-3">
        <div><div className="text-[var(--muted)] text-xs">Tokens</div><div className="font-semibold">{fmtTokens(tokens)}</div></div>
        <div><div className="text-[var(--muted)] text-xs">Cost</div><div className="font-semibold">{fmtCost(cost)}</div></div>
      </div>
      <table className="w-full text-xs">
        <thead><tr className="text-[var(--muted)] text-left"><th>Model</th><th className="text-right">Tokens</th><th className="text-right">Cost</th></tr></thead>
        <tbody>
          {models.map(([model, b]) => (
            <tr key={model}><td>{model}</td><td className="text-right">{fmtTokens(b.tokens.total)}</td><td className="text-right">{fmtCost(b.costUsd)}</td></tr>
          ))}
          {models.length === 0 && (<tr><td colSpan={3} className="text-[var(--muted)] py-2">No usage yet</td></tr>)}
        </tbody>
      </table>
    </div>
  );
}
