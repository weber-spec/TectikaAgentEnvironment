'use client';

import { useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '@/lib/api';
import type { Connection, ConnectionCatalogEntry } from '@/lib/types';
import { Button, EmptyState, Spinner } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { BrandIcon } from '@/components/ui/brand-icons';
import { ConnectionCreateModal } from '@/components/connections/ConnectionCreateModal';
import { toast } from '@/lib/toast';

type Tab = 'available' | 'active';

const CATEGORY_LABEL: Record<string, string> = {
  'model': 'Models',
  'agent-tool': 'Agent tools',
  'source-control': 'Source control',
};
const CATEGORY_ORDER = ['model', 'agent-tool', 'source-control'];

function StatusBadge({ status }: { status: Connection['status'] }) {
  const map: Record<string, string> = {
    Connected: 'bg-emerald-500/15 text-emerald-600',
    Error: 'bg-red-500/15 text-red-600',
    Disconnected: 'bg-[var(--muted)]/15 text-[var(--muted)]',
  };
  return <span className={`px-2 py-0.5 rounded-full text-[11px] font-semibold ${map[status] ?? map.Disconnected}`}>{status}</span>;
}

export default function ConnectionsPage() {
  const [tab, setTab] = useState<Tab>('available');
  const [catalog, setCatalog] = useState<ConnectionCatalogEntry[]>([]);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState<ConnectionCatalogEntry | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = async () => {
    try {
      const [cat, conns] = await Promise.all([api.connections.catalog(), api.connections.list()]);
      setCatalog(cat);
      setConnections(conns);
    } catch {
      toast('Could not load connections', 'error');
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => { load(); }, []);

  const catalogById = useMemo(() => new Map(catalog.map(c => [c.id, c])), [catalog]);

  const grouped = useMemo(() => {
    const by: Record<string, ConnectionCatalogEntry[]> = {};
    for (const e of catalog) (by[e.category] ??= []).push(e);
    return CATEGORY_ORDER.filter(c => by[c]?.length).map(c => ({ category: c, entries: by[c] }));
  }, [catalog]);

  const handleValidate = async (c: Connection) => {
    setBusyId(c.id);
    try {
      const updated = await api.connections.validate(c.id);
      setConnections(prev => prev.map(x => (x.id === c.id ? updated : x)));
      toast(updated.status === 'Connected' ? 'Connection is healthy' : 'Connection failed validation',
        updated.status === 'Connected' ? 'success' : 'error');
    } catch { toast('Validation failed', 'error'); }
    finally { setBusyId(null); }
  };

  const handleDelete = async (c: Connection) => {
    if (!confirm(`Delete connection "${c.displayName}"? Agents and boards using it will lose access.`)) return;
    setBusyId(c.id);
    try {
      await api.connections.remove(c.id);
      setConnections(prev => prev.filter(x => x.id !== c.id));
      toast('Connection deleted', 'success');
    } catch (err) {
      toast(err instanceof ApiError ? 'Could not delete the connection' : 'Could not delete the connection', 'error');
    } finally { setBusyId(null); }
  };

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="px-8 pt-6 pb-3 shrink-0">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Connections</h1>
        <p className="text-sm text-[var(--muted)] mt-1">
          Connect the models and tools your agents and boards use. Define a connection once — reference it anywhere.
        </p>
        {/* Tabs */}
        <div className="flex gap-1 mt-4 border-b border-[var(--border)]">
          {([['available', 'Available'], ['active', 'Active']] as [Tab, string][]).map(([id, label]) => (
            <button
              key={id}
              onClick={() => setTab(id)}
              className={`px-4 py-2 text-sm font-medium -mb-px border-b-2 transition-colors ${
                tab === id
                  ? 'border-[var(--primary)] text-[var(--primary)]'
                  : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'
              }`}
            >
              {label}
              {id === 'active' && connections.length > 0 && (
                <span className="ml-1.5 text-[11px] text-[var(--muted)]">{connections.length}</span>
              )}
            </button>
          ))}
        </div>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-auto px-8 pb-8">
        {loading ? (
          <div className="flex items-center justify-center h-40"><Spinner size={24} /></div>
        ) : tab === 'available' ? (
          <div className="flex flex-col gap-8 pt-4">
            {grouped.map(({ category, entries }) => (
              <section key={category}>
                <h2 className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold mb-3">
                  {CATEGORY_LABEL[category] ?? category}
                </h2>
                <div className="grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-3">
                  {entries.map(e => (
                    <button
                      key={e.id}
                      onClick={() => setCreating(e)}
                      className="text-left bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 hover:shadow-md hover:border-[var(--primary)] transition-all"
                    >
                      <div className="flex items-center gap-3 mb-2">
                        <BrandIcon name={e.iconKey} size={36} />
                        <div className="min-w-0">
                          <h3 className="font-semibold text-[var(--foreground)] truncate">{e.displayName}</h3>
                          <p className="text-[11px] text-[var(--muted)]">
                            {e.readToolCount + e.writeToolCount > 0
                              ? `${e.readToolCount} read · ${e.writeToolCount} write tools`
                              : 'Model provider'}
                          </p>
                        </div>
                      </div>
                      <p className="text-[12px] text-[var(--muted)] line-clamp-2">{e.description}</p>
                    </button>
                  ))}
                </div>
              </section>
            ))}
          </div>
        ) : (
          connections.length === 0 ? (
            <div className="pt-10">
              <EmptyState
                icon={<Icon.link size={48} />}
                title="No connections yet"
                description="Head to the Available tab to connect your first model or tool."
                action={<Button variant="primary" onClick={() => setTab('available')}>Browse available</Button>}
              />
            </div>
          ) : (
            <div className="pt-4 flex flex-col gap-2">
              {connections.map(c => {
                const cat = catalogById.get(c.catalogId);
                return (
                  <div key={c.id} className="flex items-center gap-4 bg-[var(--background)] rounded-xl border border-[var(--border)] px-4 py-3">
                    <BrandIcon name={cat?.iconKey ?? c.catalogId} size={36} />
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-semibold text-[var(--foreground)] truncate">{c.displayName}</span>
                        {c.isSystem && <span className="text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] font-medium">System</span>}
                        {c.scope === 'Private' && <span className="text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] font-medium">Private</span>}
                      </div>
                      <p className="text-[12px] text-[var(--muted)] truncate">
                        {c.metadata?.foundryType ?? cat?.displayName ?? c.catalogId}
                        {!c.isSystem && c.createdAt && <> · created {new Date(c.createdAt).toLocaleDateString()}</>}
                      </p>
                    </div>
                    <StatusBadge status={c.status} />
                    {!c.isSystem && (
                      <div className="flex items-center gap-1 shrink-0">
                        <Button size="sm" onClick={() => handleValidate(c)} disabled={busyId === c.id}>
                          {busyId === c.id ? '…' : 'Test'}
                        </Button>
                        <Button size="sm" variant="danger" onClick={() => handleDelete(c)} disabled={busyId === c.id}>Delete</Button>
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )
        )}
      </div>

      {creating && (
        <ConnectionCreateModal
          entry={creating}
          onClose={() => setCreating(null)}
          onCreated={load}
        />
      )}
    </div>
  );
}
