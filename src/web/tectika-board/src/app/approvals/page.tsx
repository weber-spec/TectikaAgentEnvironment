'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { Approval, HumanInteraction } from '@/lib/types';
import { Button, Skeleton, EmptyState, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { InteractionCard } from '@/components/InteractionCard';
import { relativeTime, displayName, daysUntil } from '@/lib/format';
import { colorFor } from '@/lib/palette';
import { toast } from '@/lib/toast';

export default function ApprovalsPage() {
  const [approvals, setApprovals] = useState<Approval[] | null>(null);
  const [interactions, setInteractions] = useState<HumanInteraction[] | null>(null);
  const [approvalsError, setApprovalsError] = useState(false);
  const [interactionsError, setInteractionsError] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    api.approvals.pending()
      .then(setApprovals)
      .catch(() => { setApprovals([]); setApprovalsError(true); });
    api.interactions.pending()
      .then(setInteractions)
      .catch(() => { setInteractions([]); setInteractionsError(true); });
  }, []);

  const respondApproval = async (a: Approval, approved: boolean) => {
    setBusy(a.id);
    try {
      await api.approvals.respond(a.id, a.runId, approved);
      setApprovals(prev => (prev ?? []).filter(x => x.id !== a.id));
      toast(approved ? 'Approved' : 'Rejected', approved ? 'success' : 'info');
    } catch { toast('Could not submit decision', 'error'); }
    finally { setBusy(null); }
  };

  const removeInteraction = (id: string) => {
    setInteractions(prev => (prev ?? []).filter(x => x.id !== id));
  };

  // Still loading if either list is null
  const loading = approvals === null || interactions === null;

  const totalCount = (approvals?.length ?? 0) + (interactions?.length ?? 0);
  const partialError = !loading && (approvalsError || interactionsError);

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Pending Interactions</h1>
        <p className="text-sm text-[var(--muted)] mt-0.5">Human-in-the-loop gates waiting for your sign-off.</p>
      </div>

      <div className="px-8 pb-8 flex-1 max-w-3xl">
        {partialError && (
          <div className="mb-4 rounded-lg border border-[#e2445c33] bg-[#e2445c0a] px-4 py-2.5 text-xs text-[#e2445c] flex items-center gap-2">
            <Icon.warning size={14} />
            {approvalsError && interactionsError
              ? 'Could not load pending items. Showing partial results.'
              : approvalsError
                ? 'Could not load legacy approvals. Showing interactions only.'
                : 'Could not load interactions. Showing legacy approvals only.'}
          </div>
        )}

        {loading ? (
          <div className="flex flex-col gap-3">
            {[...Array(3)].map((_, i) => <Skeleton key={i} className="h-28" />)}
          </div>
        ) : totalCount === 0 ? (
          <EmptyState
            icon={<Icon.approvals size={48} />}
            title="All caught up"
            description="No pending interactions. Sensitive agent actions will show up here for your review."
          />
        ) : (
          <div className="flex flex-col gap-3">
            {/* Legacy approval cards */}
            {(approvals ?? []).map(a => {
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
                    <Button variant="danger" size="sm" disabled={busy === a.id} onClick={() => respondApproval(a, false)}><Icon.x size={15} /> Reject</Button>
                    <Button variant="primary" size="sm" disabled={busy === a.id} onClick={() => respondApproval(a, true)}><Icon.check size={15} /> Approve</Button>
                  </div>
                </div>
              );
            })}

            {/* New HumanInteraction cards */}
            {(interactions ?? []).map(interaction => (
              <InteractionCard
                key={interaction.id}
                interaction={interaction}
                onResponded={() => removeInteraction(interaction.id)}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
