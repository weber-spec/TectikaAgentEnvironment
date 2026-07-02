'use client';

import { useEffect, useMemo, useState } from 'react';
import { api } from '@/lib/api';
import type { ToolItem, ToolsCatalog, ToolSource } from '@/lib/types';
import { Spinner } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { BrandIcon } from '@/components/ui/brand-icons';
import { toast } from '@/lib/toast';

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

function ToolRow({ tool, onToggle }: { tool: ToolItem; onToggle: (t: ToolItem, enabled: boolean) => void }) {
  const hint = setupHint(tool);
  return (
    <div className="flex items-start gap-3 bg-[var(--background)] rounded-xl border border-[var(--border)] px-4 py-3">
      {tool.source !== 'board' && <BrandIcon name={tool.iconKey ?? 'foundry'} size={30} />}
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-sm font-medium text-[var(--foreground)] font-mono">{tool.name}</span>
          {tool.isWrite === true && <span className="text-[10px] px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-600 font-medium">write</span>}
          {tool.isWrite === false && <span className="text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] font-medium">read</span>}
          {hint && <span className="text-[10px] px-1.5 py-0.5 rounded bg-[var(--surface)] text-[var(--muted)] font-medium">{hint}</span>}
        </div>
        <p className="text-[12px] text-[var(--muted)] mt-0.5 line-clamp-2">{tool.description}</p>
      </div>
      {tool.lockable ? (
        <ToggleSwitch on={tool.enabled} onChange={v => onToggle(tool, v)} />
      ) : (
        <span className="flex items-center gap-1 text-[11px] text-[var(--muted)] shrink-0" title="Core tool — always on">
          <Icon.unlock size={13} /> always on
        </span>
      )}
    </div>
  );
}

export default function ToolsPage() {
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

  const Section = ({ title, tools, blurb }: { title: string; tools: ToolItem[]; blurb?: string }) => (
    <section>
      <h2 className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold mb-1">{title}</h2>
      {blurb && <p className="text-[12px] text-[var(--muted)] mb-3">{blurb}</p>}
      <div className="flex flex-col gap-2">{tools.map(t => <ToolRow key={t.toolId} tool={t} onToggle={onToggle} />)}</div>
    </section>
  );

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <div className="px-8 pt-6 pb-3 shrink-0">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Tools</h1>
        <p className="text-sm text-[var(--muted)] mt-1">
          Every capability your agents can use — board tools, Foundry built-ins, and connected integrations.
          Toggle availability org-wide here; enable per agent in the agent editor.
        </p>
      </div>

      <div className="flex-1 overflow-auto px-8 pb-8">
        {!catalog ? (
          <div className="flex items-center justify-center h-40"><Spinner size={24} /></div>
        ) : (
          <div className="flex flex-col gap-8 pt-2">
            {/* Board tools — grouped */}
            <div className="flex flex-col gap-6">
              <h2 className="text-sm font-bold text-[var(--foreground)]">Board tools <span className="text-[var(--muted)] font-normal">· Tectika</span></h2>
              {boardByGroup.map(({ group, tools }) => (
                <Section key={group} title={BOARD_GROUP_LABEL[group] ?? group} tools={tools} />
              ))}
            </div>

            <Section
              title="Foundry built-in · Foundry agents"
              blurb="Generic capabilities the Azure AI Foundry platform provides. Off by default — enable to make them available, then turn them on per Foundry agent."
              tools={catalog.foundry}
            />

            <Section
              title="Integration tools · connected services"
              blurb="Tools reached through a connection (Connections page). Actions run as the connected account."
              tools={catalog.integration}
            />
          </div>
        )}
      </div>
    </div>
  );
}
