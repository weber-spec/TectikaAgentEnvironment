'use client';
import { useEffect, useState } from 'react';
import { useBoard } from '@/lib/board-context';
import { formatCurrency, formatCompact } from '@/lib/format';
import type { UsageBucket } from '@/lib/types';

type View = 'session' | 'lifetime';

export function UsagePanel({ taskId }: { taskId: string }) {
  // Read from the board's live usage map (kept fresh by SSE + the reconcile poll), and
  // kick a refresh on open so the numbers are current immediately.
  const { usageByTaskId, refreshUsage } = useBoard();
  const rollup = usageByTaskId[taskId];
  const [view, setView] = useState<View>('session');

  useEffect(() => { void refreshUsage(taskId); }, [taskId, refreshUsage]);

  const session = rollup?.currentSession ?? undefined;
  const bucket: UsageBucket | undefined = view === 'session' ? session : rollup?.lifetime;
  const perModel = Object.entries(
    (view === 'session' ? session?.perModel : rollup?.perModel) ?? {},
  ).sort((a, b) => b[1].costUsd - a[1].costUsd);
  const empty = !bucket || bucket.eventCount === 0;

  return (
    <div className="rounded-lg border border-[var(--border)] p-3 bg-[var(--background)]">
      <div className="flex items-center justify-between mb-3">
        <span className="font-semibold text-[var(--foreground)]">Usage</span>
        <div className="inline-flex rounded-md border border-[var(--border)] overflow-hidden text-xs" role="tablist" aria-label="Usage scope">
          {([['session', 'This session'], ['lifetime', 'Task lifetime']] as const).map(([v, label], i) => (
            <button
              key={v}
              type="button"
              role="tab"
              aria-selected={view === v}
              onClick={() => setView(v)}
              className={[
                'px-3 py-1 font-medium transition-colors',
                i > 0 ? 'border-l border-[var(--border)]' : '',
                view === v
                  ? 'bg-[var(--accent)] text-white'
                  : 'bg-[var(--background)] text-[var(--muted)] hover:text-[var(--foreground)]',
              ].join(' ')}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      {empty ? (
        <p className="text-xs text-[var(--muted)] text-center py-3">
          {view === 'session' ? 'No usage in the current session yet.' : 'No usage recorded yet.'}
        </p>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-2 text-center mb-3">
            <div>
              <div className="text-[var(--muted)] text-xs">Tokens</div>
              <div className="font-semibold text-[var(--foreground)]">{formatCompact(bucket?.tokens.total)}</div>
            </div>
            <div>
              <div className="text-[var(--muted)] text-xs">Cost</div>
              <div className="font-semibold text-[var(--foreground)]">{formatCurrency(bucket?.costUsd)}</div>
            </div>
          </div>
          <p className="text-[10px] text-[var(--muted)] uppercase tracking-wide font-semibold mb-1">
            By model · {view === 'session' ? 'this session' : 'lifetime'}
          </p>
          <table className="w-full text-xs">
            <thead>
              <tr className="text-[var(--muted)] text-left">
                <th className="font-medium">Model</th>
                <th className="font-medium text-right">Tokens</th>
                <th className="font-medium text-right">Cost</th>
              </tr>
            </thead>
            <tbody>
              {perModel.map(([model, b]) => (
                <tr key={model}>
                  <td className="text-[var(--foreground)]">{model}</td>
                  <td className="text-right">{formatCompact(b.tokens.total)}</td>
                  <td className="text-right">{formatCurrency(b.costUsd)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  );
}
