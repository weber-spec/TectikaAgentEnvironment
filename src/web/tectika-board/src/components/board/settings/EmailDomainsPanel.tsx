'use client';

import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api';
import type { ResendDomain } from '@/lib/types';
import { toast } from '@/lib/toast';

const VERIFIED = new Set(['verified']);
const FAILED = new Set(['failed', 'partially_failed', 'temporary_failure']);

function badge(status: string) {
  const color = VERIFIED.has(status) ? '#00c875' : FAILED.has(status) ? '#e2445c' : '#fdab3d';
  return <span className="text-[11px] font-semibold" style={{ color }}>● {status}</span>;
}

export function EmailDomainsPanel({ boardId, defaultFrom }: { boardId: string; defaultFrom?: string }) {
  const [domains, setDomains] = useState<ResendDomain[]>([]);
  const [name, setName] = useState('');
  const [from, setFrom] = useState(defaultFrom ?? '');
  const [busy, setBusy] = useState(false);

  const load = async () => {
    try {
      const list = await api.email.domains(boardId);
      // The list endpoint omits DNS records; hydrate them for unverified domains so the records
      // to add stay visible across reloads (and reflect the latest verification status).
      const hydrated = await Promise.all(
        list.map(d => VERIFIED.has(d.status)
          ? Promise.resolve(d)
          : api.email.getDomain(boardId, d.id).catch(() => d)),
      );
      setDomains(hydrated);
    } catch (err) { if (err instanceof ApiError) toast('Could not load domains', 'error'); }
  };
  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [boardId]);

  const add = async () => {
    if (!name.trim()) return;
    setBusy(true);
    try { const d = await api.email.addDomain(boardId, name.trim()); setName(''); setDomains(p => [...p.filter(x => x.id !== d.id), d]); }
    catch { toast('Could not add domain', 'error'); }
    finally { setBusy(false); }
  };

  const verify = async (id: string) => {
    try { const d = await api.email.verifyDomain(boardId, id); setDomains(p => p.map(x => x.id === id ? d : x)); toast('Verification started — click Refresh in a minute to check', 'info'); }
    catch { toast('Could not verify', 'error'); }
  };

  const refresh = async (id: string) => {
    try { const d = await api.email.getDomain(boardId, id); setDomains(p => p.map(x => x.id === id ? d : x)); }
    catch { toast('Could not refresh status', 'error'); }
  };

  const remove = async (id: string) => {
    try { await api.email.deleteDomain(boardId, id); setDomains(p => p.filter(x => x.id !== id)); }
    catch { toast('Could not remove', 'error'); }
  };

  const saveFrom = async () => {
    try { await api.email.setFrom(boardId, from.trim()); toast('Default sender saved', 'success'); }
    catch { toast('Could not save sender', 'error'); }
  };

  return (
    <div className="flex flex-col gap-4 mt-3">
      <div>
        <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Sending domains</span>
        <div className="flex gap-2 mt-1">
          <input className="inp flex-1" placeholder="yourdomain.com" value={name} onChange={e => setName(e.target.value)} />
          <Button onClick={add} disabled={busy || !name.trim()}>Add</Button>
        </div>
      </div>

      {domains.map(d => (
        <div key={d.id} className="rounded-lg border border-[var(--border)] p-3">
          <div className="flex items-center justify-between">
            <span className="font-semibold text-[13px]">{d.name}</span>
            <span className="flex items-center gap-3">{badge(d.status)}
              {!VERIFIED.has(d.status) && <button className="text-[12px] underline" onClick={() => verify(d.id)}>Verify</button>}
              {!VERIFIED.has(d.status) && <button className="text-[12px] underline" onClick={() => refresh(d.id)}>Refresh</button>}
              <button className="text-[12px] text-[var(--muted)] underline" onClick={() => remove(d.id)}>Remove</button>
            </span>
          </div>
          {!VERIFIED.has(d.status) && d.records?.length ? (
            <div className="mt-2">
              <p className="text-[11px] text-[var(--muted)] mb-1">Add these DNS records at your domain host, then click Verify:</p>
              <div className="text-[11px] font-mono overflow-x-auto">
                {d.records.map((r, i) => (
                  <div key={i} className="grid grid-cols-[60px_1fr_2fr] gap-2 py-0.5 border-t border-[var(--border)]">
                    <span>{r.type}</span><span className="truncate">{r.name}</span><span className="truncate">{r.value}</span>
                  </div>
                ))}
              </div>
            </div>
          ) : null}
        </div>
      ))}

      <div>
        <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Default sender (From)</span>
        <div className="flex gap-2 mt-1">
          <input className="inp flex-1" placeholder="Agents <agents@yourdomain.com>" value={from} onChange={e => setFrom(e.target.value)} />
          <Button onClick={saveFrom} disabled={!from.trim()}>Save</Button>
        </div>
        <p className="text-[11px] text-[var(--muted)] mt-1">Agents send from this address unless they specify another on a verified domain.</p>
      </div>
    </div>
  );
}
