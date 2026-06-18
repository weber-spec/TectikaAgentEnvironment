import type { UsageRollup } from '@/lib/types';
import { formatCurrency, formatCompact } from '@/lib/format';

/** Shared "Cost by model" breakdown table used by Dashboards and Analytics pages. */
export function ModelBreakdownTable({ usage }: { usage: UsageRollup | null }) {
  if (!usage || Object.keys(usage.perModel).length === 0) {
    return <p className="text-sm text-[var(--muted)]">No usage yet.</p>;
  }
  return (
    <div className="flex flex-col w-full">
      <div className="grid grid-cols-12 text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold py-2 border-b border-[var(--border)]">
        <span className="col-span-6">Model</span>
        <span className="col-span-3 text-right">Tokens</span>
        <span className="col-span-3 text-right">Cost</span>
      </div>
      {Object.entries(usage.perModel).sort(([, a], [, b]) => b.costUsd - a.costUsd).map(([model, bucket]) => (
        <div key={model} className="grid grid-cols-12 items-center py-2 border-b border-[var(--border)] last:border-0 text-sm">
          <span className="col-span-6 text-[var(--foreground)] font-medium truncate">{model}</span>
          <span className="col-span-3 text-right text-[var(--muted)]">{formatCompact(bucket.tokens.total)}</span>
          <span className="col-span-3 text-right text-[var(--foreground)]">{formatCurrency(bucket.costUsd)}</span>
        </div>
      ))}
    </div>
  );
}
