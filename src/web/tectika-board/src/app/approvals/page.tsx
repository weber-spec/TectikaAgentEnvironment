'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { Approval } from '@/lib/types';
import { Button, Skeleton, EmptyState, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { relativeTime, displayName, daysUntil } from '@/lib/format';
import { colorFor } from '@/lib/palette';
import { toast } from '@/lib/toast';

export default function ApprovalsPage() {
  const [approvals, setApprovals] = useState<Approval[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => { api.approvals.pending().then(setApprovals).catch(() => setApprovals([])); }, []);

  const respond = async (a: Approval, approved: boolean) => {
    setBusy(a.id);
    try {
      await api.approvals.respond(a.id, a.runId, approved);
      setApprovals(prev => (prev ?? []).filter(x => x.id !== a.id));
      toast(approved ? 'Approved' : 'Rejected', approved ? 'success' : 'info');
    } catch { toast('Could not submit decision', 'error'); }
    finally { setBusy(null); }
  };

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Approvals</h1>
        <p className="text-sm text-[var(--muted)] mt-0.5">Human-in-the-loop gates waiting for your sign-off.</p>
      </div>
      <div className="px-8 pb-8 flex-1 max-w-3xl">
        {approvals === null ? (
          <div className="flex flex-col gap-3">{[...Array(3)].map((_, i) => <Skeleton key={i} className="h-28" />)}</div>
        ) : approvals.length === 0 ? (
          <EmptyState icon={<Icon.approvals size={48} />} title="Inbox zero" description="No approvals are waiting. Sensitive agent actions will show up here for your review." />
        ) : (
          <div className="flex flex-col gap-3">
            {approvals.map(a => {
              const expiry = daysUntil(a.expiresAt);
              return (
                <div key={a.id} className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 flex flex-col gap-3" style={{ borderLeft: '4px solid #a25ddc' }}>
                  <div className="flex items-start gap-3">
                    <span className="w-9 h-9 rounded-lg bg-[#a25ddc22] text-[#a25ddc] flex items-center justify-center shrink-0"><Icon.warning size={18} /></span>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-semibold text-[var(--foreground)]">{a.actionDescription}</p>
                      <div className="flex items-center gap-3 mt-1.5 text-[11px] text-[var(--muted)] flex-wrap">
                        <span className="inline-flex items-center gap-1"><Icon.clock size={12} /> requested {relativeTime(a.requestedAt)}</span>
                        {expiry != null && <span className={expiry <= 1 ? 'text-[#e2445c]' : ''}>expires in {expiry}d</span>}
                        {a.identityToBeUsed && <span className="inline-flex items-center gap-1"><Icon.user size={12} /> as {displayName(a.identityToBeUsed)}</span>}
                      </div>
                    </div>
                    <div className="flex items-center -space-x-2">
                      {a.requestedFrom.map(p => <Avatar key={p} name={displayName(p)} hex={colorFor(p)} size={26} ring />)}
                    </div>
                  </div>
                  <div className="flex items-center justify-end gap-2">
                    <Button variant="danger" size="sm" disabled={busy === a.id} onClick={() => respond(a, false)}><Icon.x size={15} /> Reject</Button>
                    <Button variant="primary" size="sm" disabled={busy === a.id} onClick={() => respond(a, true)}><Icon.check size={15} /> Approve</Button>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
