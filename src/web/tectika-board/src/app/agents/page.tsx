'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { AgentRole } from '@/lib/types';
import { colorFor } from '@/lib/palette';
import { Avatar, Button, Skeleton, EmptyState, Tag } from '@/components/ui/primitives';
import { Modal } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { toast } from '@/lib/toast';

const MODELS = ['gpt-4o', 'claude-opus-4-8', 'claude-sonnet-4-6', 'claude-haiku-4-5', 'o3'];

/** Per-role sync state set after a successful upsert. */
type SyncState = { synced: boolean; error?: string | null };

export default function AgentsPage() {
  const [roles, setRoles] = useState<AgentRole[] | null>(null);
  const [editing, setEditing] = useState<AgentRole | null>(null);
  const [syncStates, setSyncStates] = useState<Record<string, SyncState>>({});

  useEffect(() => { api.agentRoles.list().then(setRoles).catch(() => setRoles([])); }, []);

  const save = async (role: AgentRole) => {
    try {
      const result = await api.agentRoles.upsertFull(role);
      const saved = result.role;
      setRoles(prev => { const list = prev ?? []; return list.some(r => r.id === saved.id) ? list.map(r => r.id === saved.id ? saved : r) : [...list, saved]; });
      setSyncStates(prev => ({ ...prev, [saved.id]: { synced: result.synced, error: result.error } }));
      setEditing(null);
      toast('Agent saved', 'success');
    } catch { toast('Could not save agent', 'error'); }
  };

  const newAgent = (): AgentRole => ({
    id: `role-${Date.now().toString(36)}`, tenantId: 'default', displayName: 'New Agent',
    systemPrompt: 'You are a helpful agent.', tools: [], mcpServers: [],
    permissions: { canPushCode: false, canDeploy: false, requiresOboFor: [], requiresApprovalFor: [] },
    modelOverride: 'gpt-4o',
  });

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5 flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-[var(--foreground)]">Agents</h1>
          <p className="text-sm text-[var(--muted)] mt-0.5">Reusable AI worker configurations — prompts, models, tools and permissions.</p>
        </div>
        <Button variant="primary" onClick={() => setEditing(newAgent())}><Icon.plus size={16} /> New agent</Button>
      </div>
      <div className="px-8 pb-8 flex-1">
        {roles === null ? (
          <div className="grid gap-4" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))' }}>{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-52" />)}</div>
        ) : roles.length === 0 ? (
          <EmptyState icon={<Icon.robot size={48} />} title="No agents yet" description="Create your first agent configuration." action={<Button variant="primary" onClick={() => setEditing(newAgent())}>New agent</Button>} />
        ) : (
          <div className="grid gap-4" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))' }}>
            {roles.map(r => <RoleCard key={r.id} role={r} syncState={syncStates[r.id]} onEdit={() => setEditing(r)} />)}
          </div>
        )}
      </div>
      {editing && <RoleEditor role={editing} onSave={save} onClose={() => setEditing(null)} />}
    </div>
  );
}

function RoleCard({ role, syncState, onEdit }: { role: AgentRole; syncState?: SyncState; onEdit: () => void }) {
  // Derive a synced indicator: explicit syncState from last save takes priority,
  // otherwise fall back to presence of foundryAgentId as a passive indicator.
  const isSynced = syncState ? syncState.synced : !!role.foundryAgentId;
  const syncError = syncState?.error;
  const showSyncBadge = syncState !== undefined || !!role.foundryAgentId;

  return (
    <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 hover:shadow-md transition-shadow flex flex-col">
      <div className="flex items-center gap-3 mb-3">
        <Avatar person={{ id: role.id, name: role.displayName, kind: 'Agent', hex: colorFor(role.id) }} size={40} />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <h3 className="font-semibold text-[var(--foreground)] truncate">{role.displayName}</h3>
            {showSyncBadge && (
              isSynced
                ? <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 shrink-0">&#10003; synced</span>
                : <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded-full bg-red-500/15 text-red-600 dark:text-red-400 shrink-0" title={syncError ?? undefined}>&#9888; not synced{syncError ? `: ${syncError}` : ''}</span>
            )}
          </div>
          <span className="text-[11px] text-[var(--muted)] inline-flex items-center gap-1"><Icon.bolt size={11} /> {role.modelOverride ?? 'default model'}</span>
        </div>
        <button onClick={onEdit} className="text-[var(--muted)] hover:text-[var(--primary)] w-8 h-8 flex items-center justify-center rounded-md hover:bg-[var(--surface)]"><Icon.edit size={16} /></button>
      </div>
      <p className="text-xs text-[var(--muted)] line-clamp-3 mb-3 flex-1">{role.systemPrompt}</p>
      <div className="flex flex-wrap gap-1 mb-2">
        {role.tools.map(t => <Tag key={t} label={t} hex="#0086c0" />)}
        {role.mcpServers.map(m => <Tag key={m} label={`mcp:${m}`} hex="#a25ddc" />)}
      </div>
      <div className="flex flex-wrap gap-1">
        {role.permissions.canPushCode && <Tag label="push code" hex="#fdab3d" />}
        {role.permissions.canDeploy && <Tag label="deploy" hex="#e2445c" />}
        {role.permissions.requiresApprovalFor.map(a => <Tag key={a} label={`approval: ${a}`} hex="#784bd1" />)}
      </div>
    </div>
  );
}

function RoleEditor({ role, onSave, onClose }: { role: AgentRole; onSave: (r: AgentRole) => void; onClose: () => void }) {
  const [r, setR] = useState<AgentRole>(role);
  const set = (p: Partial<AgentRole>) => setR(prev => ({ ...prev, ...p }));
  return (
    <Modal open onClose={onClose} width={560} title={<span className="flex items-center gap-2"><Icon.robot size={18} /> {role.displayName === 'New Agent' ? 'New agent' : 'Edit agent'}</span>}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" onClick={() => onSave(r)}>Save agent</Button></>}>
      <div className="flex flex-col gap-3">
        <L label="Display name"><input value={r.displayName} onChange={e => set({ displayName: e.target.value })} className="inp" /></L>
        <L label="System prompt"><textarea value={r.systemPrompt} onChange={e => set({ systemPrompt: e.target.value })} rows={4} className="inp resize-none" /></L>
        <div className="grid grid-cols-2 gap-3">
          <L label="Model"><select value={r.modelOverride ?? ''} onChange={e => set({ modelOverride: e.target.value })} className="inp">{MODELS.map(m => <option key={m} value={m}>{m}</option>)}</select></L>
          <L label="Tools (comma-sep)"><input value={r.tools.join(', ')} onChange={e => set({ tools: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })} className="inp" /></L>
        </div>
        <L label="MCP servers (comma-sep)"><input value={r.mcpServers.join(', ')} onChange={e => set({ mcpServers: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })} className="inp" /></L>
        <div className="flex gap-4 pt-1">
          <label className="flex items-center gap-2 text-sm text-[var(--foreground)]"><input type="checkbox" checked={r.permissions.canPushCode} onChange={e => set({ permissions: { ...r.permissions, canPushCode: e.target.checked } })} className="accent-[var(--primary)]" /> Can push code</label>
          <label className="flex items-center gap-2 text-sm text-[var(--foreground)]"><input type="checkbox" checked={r.permissions.canDeploy} onChange={e => set({ permissions: { ...r.permissions, canDeploy: e.target.checked } })} className="accent-[var(--primary)]" /> Can deploy</label>
        </div>
      </div>
    </Modal>
  );
}

function L({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">{label}</span><div className="mt-1">{children}</div></label>;
}
