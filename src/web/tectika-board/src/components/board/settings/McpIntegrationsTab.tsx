'use client';

import { useEffect, useState, useCallback } from 'react';
import { Button } from '@/components/ui/primitives';
import { BrandIcon } from '@/components/ui/brand-icons';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { Connection, ConnectionCatalogEntry } from '@/lib/types';
import { EmailDomainsPanel } from './EmailDomainsPanel';

/// Board integrations = enabling tenant connections for this board. Credentials live in the tenant registry
/// (Connections page); here a board owner chooses which of them this board's agents may use.
export function McpIntegrationsTab({ boardId }: { boardId: string }) {
  const [conns, setConns] = useState<Connection[] | null>(null);
  const [catMap, setCatMap] = useState<Record<string, ConnectionCatalogEntry>>({});
  const [enabled, setEnabled] = useState<Set<string>>(new Set());
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [all, cat, bindings] = await Promise.all([
        api.connections.list(), api.connections.catalog(), api.boardConnections.list(boardId),
      ]);
      setConns(all.filter(c => c.category === 'agent-tool'));
      setCatMap(Object.fromEntries(cat.map(c => [c.id, c])));
      setEnabled(new Set(bindings.map(b => b.connectionId)));
    } catch { setConns([]); }
  }, [boardId]);

  // eslint-disable-next-line react-hooks/set-state-in-effect -- load only setState after an await (async), not synchronously
  useEffect(() => { void load(); }, [load]);

  const toggle = async (conn: Connection, on: boolean) => {
    setBusyId(conn.id);
    try {
      if (on) { await api.boardConnections.bind(boardId, conn.id); setEnabled(p => new Set(p).add(conn.id)); }
      else { await api.boardConnections.unbind(boardId, conn.id); setEnabled(p => { const n = new Set(p); n.delete(conn.id); return n; }); }
    } catch { toast('Could not update the board integration', 'error'); }
    finally { setBusyId(null); }
  };

  if (conns === null) return <p className="text-sm text-[var(--muted)]">Loading integrations…</p>;

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-[var(--muted)]">
        Enable connections for this board. Define credentials once on the{' '}
        <a href="/connections" className="underline text-[var(--primary)]">Connections</a> page; here you choose
        which this board&apos;s agents may use. Actions run as the connected account.
      </p>

      {conns.length === 0 && (
        <p className="text-sm text-[var(--muted)]">
          No tool connections defined yet. Create one on the{' '}
          <a href="/connections" className="underline text-[var(--primary)]">Connections</a> page.
        </p>
      )}

      <div className="flex flex-col gap-2">
        {conns.map(conn => {
          const cat = catMap[conn.catalogId];
          const on = enabled.has(conn.id);
          return (
            <div key={conn.id} className="border border-[var(--border)] rounded-lg p-3">
              <div className="flex items-start justify-between gap-3">
                <div className="flex items-start gap-2 min-w-0">
                  <BrandIcon name={cat?.iconKey ?? conn.catalogId} size={32} />
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="text-sm font-medium text-[var(--foreground)]">{conn.displayName}</span>
                      {on && <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400">enabled</span>}
                    </div>
                    <div className="text-xs text-[var(--muted)] mt-0.5">
                      {cat?.displayName ?? conn.catalogId}
                      {cat && <> · {cat.readToolCount} read · {cat.writeToolCount} write</>}
                    </div>
                  </div>
                </div>
                <div className="shrink-0">
                  <Button variant={on ? 'danger' : 'primary'} onClick={() => toggle(conn, !on)} disabled={busyId === conn.id}>
                    {busyId === conn.id ? '…' : on ? 'Disable' : 'Enable'}
                  </Button>
                </div>
              </div>
              {conn.catalogId === 'email' && on && (
                <EmailDomainsPanel boardId={boardId} defaultFrom={conn.metadata?.defaultFrom} />
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
