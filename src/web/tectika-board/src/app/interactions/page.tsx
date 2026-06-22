'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { HumanInteraction } from '@/lib/types';
import { Skeleton, EmptyState } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { InteractionCard } from '@/components/InteractionCard';

export default function InteractionsPage() {
  const [interactions, setInteractions] = useState<HumanInteraction[] | null>(null);
  const [loadError, setLoadError] = useState(false);

  useEffect(() => {
    api.interactions.pending()
      .then(setInteractions)
      .catch(() => { setInteractions([]); setLoadError(true); });
  }, []);

  const removeInteraction = (id: string) => {
    setInteractions(prev => (prev ?? []).filter(x => x.id !== id));
  };

  const loading = interactions === null;

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Pending Interactions</h1>
        <p className="text-sm text-[var(--muted)] mt-0.5">Human-in-the-loop gates waiting for your sign-off.</p>
      </div>

      <div className="px-8 pb-8 flex-1 max-w-3xl">
        {loadError && (
          <div className="mb-4 rounded-lg border border-[#e2445c33] bg-[#e2445c0a] px-4 py-2.5 text-xs text-[#e2445c] flex items-center gap-2">
            <Icon.warning size={14} />
            Could not load pending interactions. Please retry.
          </div>
        )}

        {loading ? (
          <div className="flex flex-col gap-3">
            {[...Array(3)].map((_, i) => <Skeleton key={i} className="h-28" />)}
          </div>
        ) : (interactions ?? []).length === 0 ? (
          <EmptyState
            icon={<Icon.approvals size={48} />}
            title="All caught up"
            description="No pending interactions. Sensitive agent actions will show up here for your review."
          />
        ) : (
          <div className="flex flex-col gap-3">
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
