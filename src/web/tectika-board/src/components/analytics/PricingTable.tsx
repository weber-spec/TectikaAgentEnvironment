'use client';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { PricingCatalog } from '@/lib/types';

/** Read-only model pricing catalog. Rendered as the "Pricing" tab of the Analytics page. */
export function PricingTable() {
  const [catalog, setCatalog] = useState<PricingCatalog | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  useEffect(() => {
    api.usage.pricing()
      .then(c => { setCatalog(c); setLoading(false); })
      .catch(() => { setError(true); setLoading(false); });
  }, []);
  if (loading) return <p className="text-sm text-[var(--muted)]">Loading…</p>;
  if (error || !catalog) return <p className="text-sm text-[var(--muted)]">Could not load pricing.</p>;
  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-[var(--muted)]">
        Catalog version {catalog.version}. Read-only — rates are managed in the repository. Past costs are frozen at the rate in effect when they were incurred.
      </p>
      <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-[var(--muted)] border-b border-[var(--border)] bg-[var(--surface)]">
              <th className="px-4 py-3 font-semibold">Provider</th>
              <th className="px-4 py-3 font-semibold">Model</th>
              <th className="px-4 py-3 text-right font-semibold">Input /1M</th>
              <th className="px-4 py-3 text-right font-semibold">Cached /1M</th>
              <th className="px-4 py-3 text-right font-semibold">Output /1M</th>
              <th className="px-4 py-3 font-semibold">Currency</th>
              <th className="px-4 py-3 font-semibold">Effective</th>
            </tr>
          </thead>
          <tbody>
            {catalog.prices.map((p, i) => (
              <tr key={i} className="border-b border-[var(--border)] last:border-0">
                <td className="px-4 py-3 text-[var(--foreground)]">{p.provider}</td>
                <td className="px-4 py-3 text-[var(--foreground)]">{p.model}{p.modelVersion ? ` (${p.modelVersion})` : ''}</td>
                <td className="px-4 py-3 text-right text-[var(--foreground)] font-mono">{p.inputPerMillion.toFixed(2)}</td>
                <td className="px-4 py-3 text-right text-[var(--foreground)] font-mono">{p.cachedInputPerMillion.toFixed(2)}</td>
                <td className="px-4 py-3 text-right text-[var(--foreground)] font-mono">{p.outputPerMillion.toFixed(2)}</td>
                <td className="px-4 py-3 text-[var(--muted)]">{p.currency}</td>
                <td className="px-4 py-3 text-[var(--muted)]">{new Date(p.effectiveFrom).toLocaleDateString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
