'use client';

import { useState } from 'react';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { BrandIcon } from '@/components/ui/brand-icons';
import { api, ApiError } from '@/lib/api';
import type { ConnectionCatalogEntry, ConnectionScope } from '@/lib/types';
import { toast } from '@/lib/toast';

/** Pull a readable message out of an ApiError whose message is "API 400: {json}". */
function extractError(err: ApiError): string {
  const brace = err.message.indexOf('{');
  if (brace >= 0) {
    try {
      const body = JSON.parse(err.message.slice(brace));
      if (body?.detail) return String(body.detail);
      if (body?.error === 'ValidationFailed') return 'That credential was rejected by the service. Check it and try again.';
      if (body?.error === 'MissingFields') return 'Please fill in all required fields.';
      if (body?.error === 'UnknownIntegration') return 'This integration is not recognized.';
      if (body?.error) return String(body.error);
    } catch { /* not JSON */ }
  }
  return 'Could not create the connection. Check the details and try again.';
}

export function ConnectionCreateModal({
  entry, onClose, onCreated,
}: {
  entry: ConnectionCatalogEntry;
  onClose: () => void;
  onCreated: () => void;
}) {
  const [displayName, setDisplayName] = useState(entry.displayName);
  const [values, setValues] = useState<Record<string, string>>({});
  const [scope, setScope] = useState<ConnectionScope>('Organization');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const setField = (name: string, v: string) => { setValues(p => ({ ...p, [name]: v })); setError(null); };
  const missingRequired = entry.authFields.some(f => f.secret && !values[f.name]?.trim());

  const handleCreate = async () => {
    if (missingRequired) return;
    setSaving(true);
    setError(null);
    try {
      const secrets: Record<string, string> = {};
      for (const f of entry.authFields) if (values[f.name]?.trim()) secrets[f.name] = values[f.name].trim();
      await api.connections.create({
        catalogId: entry.id,
        displayName: displayName.trim() || undefined,
        scope,
        secrets,
      });
      toast(`${displayName.trim() || entry.displayName} connected`, 'success');
      onCreated();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) setError(extractError(err));
      else toast('Could not create the connection', 'error');
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      width={480}
      z={1300}
      title={
        <span className="flex items-center gap-2 text-[var(--foreground)]">
          <BrandIcon name={entry.iconKey} size={24} /> Connect {entry.displayName}
        </span>
      }
      footer={
        <div className="flex justify-end gap-2 w-full">
          <Button onClick={onClose} disabled={saving}>Cancel</Button>
          <Button variant="primary" onClick={handleCreate} disabled={saving || missingRequired}>
            {saving ? 'Connecting…' : 'Create connection'}
          </Button>
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && <div className="text-sm text-[#e2445c] bg-[#e2445c11] px-3 py-2 rounded-lg">{error}</div>}
        <p className="text-[13px] text-[var(--muted)]">{entry.description}</p>

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Connection name</span>
          <input className="inp mt-1 w-full" value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder={entry.displayName} />
          {entry.supportsMultiple && (
            <p className="text-[11px] text-[var(--muted)] mt-1">Give it a distinct name — you can create several connections of this type.</p>
          )}
        </label>

        {entry.authFields.map(f => (
          <label className="block" key={f.name}>
            <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">{f.label}</span>
            <input
              className="inp mt-1 w-full"
              type={f.type === 'password' ? 'password' : 'text'}
              placeholder={f.hint}
              value={values[f.name] ?? ''}
              onChange={e => setField(f.name, e.target.value)}
            />
          </label>
        ))}

        {/* Private vs organization — stored & shown, not enforced yet (lands with Microsoft auth). */}
        <div className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Visibility</span>
          <div className="mt-1 inline-flex rounded-lg border border-[var(--border)] overflow-hidden">
            {(['Organization', 'Private'] as ConnectionScope[]).map(s => (
              <button
                key={s}
                type="button"
                onClick={() => setScope(s)}
                className={`px-3 py-1.5 text-[13px] transition-colors ${scope === s ? 'bg-[var(--primary)] text-white' : 'text-[var(--foreground)] hover:bg-[var(--surface)]'}`}
              >
                {s === 'Organization' ? 'Organization' : 'Private'}
              </button>
            ))}
          </div>
        </div>

        <p className="text-[11px] text-[var(--muted)]">
          Validated on connect and stored securely in Azure Key Vault.
          {entry.helpUrl && <> <a href={entry.helpUrl} target="_blank" rel="noreferrer" className="underline">Where do I get this?</a></>}
        </p>
      </div>
    </Modal>
  );
}
