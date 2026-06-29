'use client';

import { useState } from 'react';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api';
import type { McpCatalogEntry } from '@/lib/types';
import { toast } from '@/lib/toast';

/** Pull a readable message out of an ApiError whose message is "API 400: {json}". */
function extractError(err: ApiError): string {
  const brace = err.message.indexOf('{');
  if (brace >= 0) {
    try {
      const body = JSON.parse(err.message.slice(brace));
      if (body?.detail) return String(body.detail);
      if (body?.error === 'ValidationFailed') return 'That credential was rejected by the service. Check the token and try again.';
      if (body?.error) return String(body.error);
    } catch { /* not JSON */ }
  }
  return 'Could not connect. Check the token and try again.';
}

export function McpConnectModal({
  boardId, entry, onClose, onConnected,
}: {
  boardId: string;
  entry: McpCatalogEntry;
  onClose: () => void;
  onConnected: () => void;
}) {
  const [displayName, setDisplayName] = useState(entry.displayName);
  const [token, setToken] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleConnect = async () => {
    if (!token.trim()) return;
    setSaving(true);
    setError(null);
    try {
      await api.mcp.connect(boardId, { catalogId: entry.id, displayName: displayName.trim() || undefined, token: token.trim() });
      toast(`${entry.displayName} connected`, 'success');
      onConnected();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) setError(extractError(err));
      else { toast('Could not connect', 'error'); }
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
      title={<span className="text-[var(--foreground)]">Connect {entry.displayName}</span>}
      footer={
        <div className="flex justify-end gap-2 w-full">
          <Button onClick={onClose} disabled={saving}>Cancel</Button>
          <Button variant="primary" onClick={handleConnect} disabled={saving || !token.trim()}>
            {saving ? 'Connecting…' : 'Connect'}
          </Button>
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div className="text-sm text-[#e2445c] bg-[#e2445c11] px-3 py-2 rounded-lg">{error}</div>
        )}
        <p className="text-[13px] text-[var(--muted)]">{entry.description}</p>
        <p className="text-[12px] text-[var(--muted)] bg-[var(--surface)] px-3 py-2 rounded-lg">
          Actions run as this shared connection for the whole board (e.g. messages post as the connected bot).
        </p>

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Connection name</span>
          <input className="inp mt-1 w-full" value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder={entry.displayName} />
        </label>

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">{entry.tokenHint}</span>
          <input
            className="inp mt-1 w-full"
            type="password"
            placeholder="Paste token"
            value={token}
            onChange={e => { setToken(e.target.value); setError(null); }}
          />
          <p className="text-[11px] text-[var(--muted)] mt-1">
            Validated on connect and stored securely in Azure Key Vault.
            {entry.helpUrl && <> <a href={entry.helpUrl} target="_blank" rel="noreferrer" className="underline">Where do I get this?</a></>}
          </p>
        </label>
      </div>
    </Modal>
  );
}
