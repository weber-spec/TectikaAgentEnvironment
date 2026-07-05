'use client';

import { useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { api } from '@/lib/api';
import type { ToolItem, ToolsCatalog } from '@/lib/types';
import { Button, EmptyState, Spinner } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { BrandIcon } from '@/components/ui/brand-icons';
import { toast } from '@/lib/toast';

type Tab = 'system' | 'foundry' | 'integration';

const BOARD_GROUP_ORDER = ['Explore', 'Control', 'Workspace', 'GitHub'];
const BOARD_GROUP_LABEL: Record<string, string> = {
  Explore: 'Explore (read the board)',
  Control: 'Control & outputs',
  Workspace: 'Workspace (sandbox)',
  GitHub: 'GitHub',
};

/** Short contextual hint for the "needs setup" state, by source. */
function setupHint(t: ToolItem): string | null {
  if (!t.needsSetup) return null;
  if (t.source === 'board') return t.group === 'GitHub' ? 'needs GitHub' : 'needs workspace';
  if (t.source === 'foundry') return 'needs a Foundry connection';
  return 'no connection yet';
}

function ToggleSwitch({ on, disabled, onChange }: { on: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      role="switch" aria-checked={on} disabled={disabled}
      onClick={() => !disabled && onChange(!on)}
      className={`relative w-9 h-5 rounded-full transition-colors shrink-0 ${on ? 'bg-[var(--primary)]' : 'bg-[var(--muted-2)]'} ${disabled ? 'opacity-40 cursor-not-allowed' : ''}`}
    >
      <span className={`absolute top-0.5 left-0.5 w-4 h-4 rounded-full bg-white transition-transform ${on ? 'translate-x-4' : ''}`} />
    </button>
  );
}

function ToolCard({ tool, onToggle }: { tool: ToolItem; onToggle: (t: ToolItem, enabled: boolean) => void }) {
  const hint = setupHint(tool);
  return (
    <div className="flex flex-col bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 hover:border-[var(--primary)] transition-colors">
      <div className="flex items-start gap-2.5">
        {tool.source !== 'board' && <BrandIcon name={tool.iconKey ?? 'foundry'} size={28} />}
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1.5 flex-wrap">
            <span className="text-sm font-medium text-[var(--foreground)] font-mono break-all">{tool.name}</span>
            {tool.isWrite === true && <span className="text-[10px] px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-600 font-medium">write</span>}
            {tool.isWrite === false && <span className="text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] font-medium">read</span>}
          </div>
          {hint && <span className="inline-block text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] font-medium mt-1">{hint}</span>}
        </div>
      </div>
      <p className="text-[12px] text-[var(--muted)] mt-2 line-clamp-3 flex-1">{tool.description}</p>
      <div className="flex items-center justify-end mt-3 pt-3 border-t border-[var(--border)]">
        {tool.lockable ? (
          <ToggleSwitch on={tool.enabled} onChange={v => onToggle(tool, v)} />
        ) : (
          <span className="flex items-center gap-1 text-[11px] text-[var(--muted)]" title="Core tool — always on">
            <Icon.unlock size={13} /> always on
          </span>
        )}
      </div>
    </div>
  );
}

function CardGrid({ tools, onToggle }: { tools: ToolItem[]; onToggle: (t: ToolItem, enabled: boolean) => void }) {
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-3">
      {tools.map(t => <ToolCard key={t.toolId} tool={t} onToggle={onToggle} />)}
    </div>
  );
}

export default function ToolsPage() {
  const router = useRouter();
  const [tab, setTab] = useState<Tab>('system');
  const [catalog, setCatalog] = useState<ToolsCatalog | null>(null);

  useEffect(() => { api.tools.catalog().then(setCatalog).catch(() => toast('Could not load tools', 'error')); }, []);

  const boardByGroup = useMemo(() => {
    const by: Record<string, ToolItem[]> = {};
    for (const t of catalog?.board ?? []) (by[t.group] ??= []).push(t);
    return BOARD_GROUP_ORDER.filter(g => by[g]?.length).map(g => ({ group: g, tools: by[g] }));
  }, [catalog]);

  const onToggle = async (tool: ToolItem, enabled: boolean) => {
    // Optimistic update across whichever group holds the tool.
    const patch = (c: ToolsCatalog): ToolsCatalog => {
      const map = (arr: ToolItem[]) => arr.map(t => t.toolId === tool.toolId ? { ...t, enabled } : t);
      return { board: map(c.board), foundry: map(c.foundry), integration: map(c.integration) };
    };
    const prev = catalog!;
    setCatalog(patch(prev));
    try { await api.tools.setEnabled(tool.toolId, enabled); }
    catch { setCatalog(prev); toast('Could not update the tool', 'error'); }
  };

  const TABS: [Tab, string, number][] = [
    ['system', 'System', catalog?.board.length ?? 0],
    ['foundry', 'Foundry', catalog?.foundry.length ?? 0],
    ['integration', 'Integrations', catalog?.integration.length ?? 0],
  ];

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="px-8 pt-6 pb-3 shrink-0">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Tools</h1>
        <p className="text-sm text-[var(--muted)] mt-1">
          Every capability your agents can use — board tools, Foundry built-ins, and connected integrations.
          Toggle availability org-wide here; enable per agent in the agent editor.
        </p>
        {/* Tabs */}
        <div className="flex gap-1 mt-4 border-b border-[var(--border)]">
          {TABS.map(([id, label, count]) => (
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
              {count > 0 && <span className="ml-1.5 text-[11px] text-[var(--muted)]">{count}</span>}
            </button>
          ))}
        </div>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-auto px-8 pb-8">
        {!catalog ? (
          <div className="flex items-center justify-center h-40"><Spinner size={24} /></div>
        ) : tab === 'system' ? (
          <div className="flex flex-col gap-6 pt-4">
            <p className="text-[12px] text-[var(--muted)] -mb-2">
              Board tools built into Tectika. Core tools are always on; the rest can be toggled org-wide.
            </p>
            {boardByGroup.map(({ group, tools }) => (
              <section key={group}>
                <h2 className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold mb-3">
                  {BOARD_GROUP_LABEL[group] ?? group}
                </h2>
                <CardGrid tools={tools} onToggle={onToggle} />
              </section>
            ))}
          </div>
        ) : tab === 'foundry' ? (
          <div className="pt-4">
            <p className="text-[12px] text-[var(--muted)] mb-4">
              Generic capabilities the Azure AI Foundry platform provides. Off by default — enable to make them
              available, then turn them on per Foundry agent.
            </p>
            <CardGrid tools={catalog.foundry} onToggle={onToggle} />
          </div>
        ) : (
          catalog.integration.length === 0 ? (
            <div className="pt-10">
              <EmptyState
                icon={<Icon.link size={48} />}
                title="No integration tools yet"
                description="Integration tools appear here once you connect a service on the Connections page."
                action={<Button variant="primary" onClick={() => router.push('/connections')}>Go to Connections</Button>}
              />
            </div>
          ) : (
            <div className="pt-4">
              <p className="text-[12px] text-[var(--muted)] mb-4">
                Tools reached through a connection (Connections page). Actions run as the connected account.
              </p>
              <CardGrid tools={catalog.integration} onToggle={onToggle} />
            </div>
          )
        )}
      </div>
    </div>
  );
}
