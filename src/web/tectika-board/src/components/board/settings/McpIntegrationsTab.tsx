'use client';

import { useEffect, useState, useCallback } from 'react';
import { Button } from '@/components/ui/primitives';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';
import type { McpCatalogEntry, McpConnection } from '@/lib/types';
import { McpConnectModal } from './McpConnectModal';

export function McpIntegrationsTab({ boardId }: { boardId: string }) {
  const [catalog, setCatalog] = useState<McpCatalogEntry[] | null>(null);
  const [connections, setConnections] = useState<McpConnection[]>([]);
  const [connecting, setConnecting] = useState<McpCatalogEntry | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const refreshConnections = useCallback(async () => {
    try { setConnections(await api.mcp.connections(boardId)); } catch { /* surfaced via toast on actions */ }
  }, [boardId]);

  useEffect(() => {
    void api.mcp.catalog().then(setCatalog).catch(() => setCatalog([]));
    // eslint-disable-next-line react-hooks/set-state-in-effect -- refreshConnections only setState after an await (async), not synchronously
    void refreshConnections();
  }, [refreshConnections]);

  const disconnect = async (conn: McpConnection) => {
    setBusyId(conn.connectionId);
    try {
      await api.mcp.disconnect(boardId, conn.connectionId);
      toast('Disconnected', 'success');
      await refreshConnections();
    } catch { toast('Could not disconnect', 'error'); }
    finally { setBusyId(null); }
  };

  if (catalog === null) return <p className="text-sm text-[var(--muted)]">Loading integrations…</p>;

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-[var(--muted)]">
        Connect external tools so agents on this board can use them. Connections are shared by the whole board;
        actions run as the connected account. Enable an integration per-agent in the Agents editor.
      </p>

      <div className="flex flex-col gap-2">
        {catalog.map(entry => {
          const conn = connections.find(c => c.catalogId === entry.id);
          return (
            <div key={entry.id} className="flex items-start justify-between gap-3 border border-[var(--border)] rounded-lg p-3">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-[var(--foreground)]">{entry.displayName}</span>
                  {conn && (
                    conn.status === 'Connected'
                      ? <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400">connected</span>
                      : <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-red-500/15 text-red-600 dark:text-red-400">error</span>
                  )}
                </div>
                <div className="text-xs text-[var(--muted)] mt-0.5">{entry.description}</div>
                <div className="text-[11px] text-[var(--muted)] mt-0.5">
                  {entry.readToolCount} read · {entry.writeToolCount} write {conn ? `· "${conn.displayName}"` : ''}
                </div>
              </div>
              <div className="shrink-0">
                {conn ? (
                  <Button variant="danger" onClick={() => disconnect(conn)} disabled={busyId === conn.connectionId}>
                    {busyId === conn.connectionId ? 'Disconnecting…' : 'Disconnect'}
                  </Button>
                ) : (
                  <Button variant="primary" onClick={() => setConnecting(entry)}>Connect</Button>
                )}
              </div>
            </div>
          );
        })}
        {catalog.length === 0 && <p className="text-sm text-[var(--muted)]">No integrations available.</p>}
      </div>

      {connecting && (
        <McpConnectModal
          boardId={boardId}
          entry={connecting}
          onClose={() => setConnecting(null)}
          onConnected={refreshConnections}
        />
      )}
    </div>
  );
}
