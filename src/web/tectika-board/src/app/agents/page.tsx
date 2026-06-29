'use client';

import { useEffect, useRef, useState } from 'react';
import { api } from '@/lib/api';
import type { AgentRole, McpCatalogEntry } from '@/lib/types';
import { colorFor } from '@/lib/palette';
import { Avatar, Button, Skeleton, EmptyState, Tag } from '@/components/ui/primitives';
import { Modal } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { ModelSelect } from '@/components/ModelSelect';
import { toast } from '@/lib/toast';

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
    systemPrompt: 'You are a helpful agent.', tools: [], mcpServers: [], mcpWriteEnabled: [],
    permissions: { canUseWorkspace: false, canPushCode: false, canDeploy: false, requiresOboFor: [], requiresApprovalFor: [] },
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
        {role.mcpServers.map(m => <Tag key={m} label={role.mcpWriteEnabled?.includes(m) ? `mcp:${m} ✎` : `mcp:${m}`} hex="#a25ddc" />)}
        {role.githubPermissions && Object.values(role.githubPermissions).some(Boolean) && (
          <Tag label="GitHub" hex="#24292e" />
        )}
      </div>
      <div className="flex flex-wrap gap-1">
        {role.permissions.canUseWorkspace && (
          <Tag label="workspace + file tools" hex="#0f9d58" />
        )}
        {role.permissions.canPushCode && <Tag label="push code" hex="#fdab3d" />}
        {role.permissions.canDeploy && <Tag label="deploy" hex="#e2445c" />}
        {role.permissions.requiresApprovalFor.map(a => <Tag key={a} label={`approval: ${a}`} hex="#784bd1" />)}
      </div>
    </div>
  );
}

function RoleEditor({ role, onSave, onClose }: { role: AgentRole; onSave: (r: AgentRole) => void | Promise<void>; onClose: () => void }) {
  const [r, setR] = useState<AgentRole>(role);
  const set = (p: Partial<AgentRole>) => setR(prev => ({ ...prev, ...p }));
  const setPerms = (p: Partial<typeof r.permissions>) =>
    setR(prev => ({ ...prev, permissions: { ...prev.permissions, ...p } }));

  const [catalog, setCatalog] = useState<McpCatalogEntry[]>([]);
  useEffect(() => { api.mcp.catalog().then(setCatalog).catch(() => setCatalog([])); }, []);

  const mcpEnabled = (id: string) => r.mcpServers.includes(id);
  const toggleMcp = (id: string, on: boolean) =>
    setR(prev => ({
      ...prev,
      mcpServers: on ? [...new Set([...prev.mcpServers, id])] : prev.mcpServers.filter(x => x !== id),
      // Disabling an integration also revokes its write opt-in.
      mcpWriteEnabled: on ? prev.mcpWriteEnabled : prev.mcpWriteEnabled.filter(x => x !== id),
    }));
  const toggleMcpWrite = (id: string, on: boolean) =>
    setR(prev => ({
      ...prev,
      mcpWriteEnabled: on ? [...new Set([...prev.mcpWriteEnabled, id])] : prev.mcpWriteEnabled.filter(x => x !== id),
    }));

  const wsEnabled = r.permissions.canUseWorkspace;
  // When workspace is enabled, GitHub read is auto-granted via cascade — the checkbox is locked.
  const githubReadEffective = wsEnabled || (r.githubPermissions?.canRead ?? false);

  // Block double-submit: the ref guards rapid synchronous re-clicks (a useState flag is stale within
  // the same render); `disabled` reflects it in the UI. One Save click = one request.
  const [saving, setSaving] = useState(false);
  const savingRef = useRef(false);
  const handleSave = async () => {
    if (savingRef.current) return;
    savingRef.current = true;
    setSaving(true);
    try { await onSave(r); }
    finally { savingRef.current = false; setSaving(false); }
  };
  return (
    <Modal open onClose={onClose} width={560} title={<span className="flex items-center gap-2"><Icon.robot size={18} /> {role.displayName === 'New Agent' ? 'New agent' : 'Edit agent'}</span>}
      footer={<><Button onClick={onClose} disabled={saving}>Cancel</Button><Button variant="primary" onClick={handleSave} disabled={saving}>{saving ? 'Saving…' : 'Save agent'}</Button></>}>
      <div className="flex flex-col gap-3">
        <L label="Display name"><input value={r.displayName} onChange={e => set({ displayName: e.target.value })} className="inp" /></L>
        <L label="System prompt"><textarea value={r.systemPrompt} onChange={e => set({ systemPrompt: e.target.value })} rows={4} className="inp resize-none" /></L>
        <L label="Model"><ModelSelect value={r.modelOverride} onChange={v => set({ modelOverride: v || undefined })} selectClassName="inp" /></L>

        {/* Layer 1: Workspace */}
        <div className="rounded-lg border border-[var(--border)] p-3">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold flex items-center gap-1.5 mb-2">
            <Icon.terminal size={13} /> Workspace
          </span>
          <label className="flex items-start gap-2 text-sm text-[var(--foreground)]">
            <input type="checkbox" checked={wsEnabled}
              onChange={e => setPerms({ canUseWorkspace: e.target.checked })}
              className="accent-[var(--primary)] mt-0.5" />
            <span>
              Can use workspace
              <span className="text-[11px] text-[var(--muted)] block mt-0.5">
                Includes: run_command · read_file · write_file · edit_file · list_dir · search_code
              </span>
            </span>
          </label>
        </div>

        {/* Layer 2: GitHub — read-only access; auto-granted when workspace is on */}
        <div className="rounded-lg border border-[var(--border)] p-3">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold flex items-center gap-1.5 mb-2">
            <svg width={13} height={13} viewBox="0 0 98 96" fill="currentColor"><path fillRule="evenodd" clipRule="evenodd" d="M48.854 0C21.839 0 0 22 0 49.217c0 21.756 13.993 40.172 33.405 46.69 2.427.49 3.316-1.059 3.316-2.362 0-1.141-.08-5.052-.08-9.127-13.59 2.934-16.42-5.867-16.42-5.867-2.184-5.704-5.42-7.17-5.42-7.17-4.448-3.015.324-3.015.324-3.015 4.934.326 7.523 5.052 7.523 5.052 4.367 7.496 11.404 5.378 14.235 4.074.404-3.178 1.699-5.378 3.074-6.6-10.839-1.141-22.243-5.378-22.243-24.283 0-5.378 1.94-9.778 5.014-13.2-.485-1.222-2.184-6.275.486-13.038 0 0 4.125-1.304 13.426 5.052a46.97 46.97 0 0 1 12.214-1.63c4.125 0 8.33.571 12.213 1.63 9.302-6.356 13.427-5.052 13.427-5.052 2.67 6.763.97 11.816.485 13.038 3.155 3.422 5.015 7.822 5.015 13.2 0 18.905-11.404 23.06-22.324 24.283 1.78 1.548 3.316 4.481 3.316 9.126 0 6.6-.08 11.897-.08 13.526 0 1.304.89 2.853 3.316 2.364 19.412-6.52 33.405-24.935 33.405-46.691C97.707 22 75.788 0 48.854 0z" /></svg>
            GitHub permissions
          </span>
          <label className="flex items-center gap-2 text-sm text-[var(--foreground)]">
            <input type="checkbox" checked={githubReadEffective} disabled={wsEnabled}
              onChange={e => setR(prev => ({ ...prev, githubPermissions: { canRead: e.target.checked } }))}
              className="accent-[var(--primary)] disabled:opacity-50" />
            Read files &amp; repository
            {wsEnabled && <span className="text-[11px] text-[var(--muted)]">(auto — workspace enabled)</span>}
          </label>
        </div>

        {/* Layer 3: MCP integrations */}
        {catalog.length > 0 && (
          <div className="rounded-lg border border-[var(--border)] p-3">
            <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold flex items-center gap-1.5 mb-2">
              <Icon.bolt size={13} /> Integrations
            </span>
            <div className="flex flex-col gap-2">
              {catalog.map(entry => {
                const on = mcpEnabled(entry.id);
                return (
                  <div key={entry.id} className="flex flex-col gap-1">
                    <label className="flex items-center gap-2 text-sm text-[var(--foreground)]">
                      <input type="checkbox" checked={on}
                        onChange={e => toggleMcp(entry.id, e.target.checked)}
                        className="accent-[var(--primary)]" />
                      {entry.displayName}
                      <span className="text-[11px] text-[var(--muted)]">({entry.readToolCount} read · {entry.writeToolCount} write)</span>
                    </label>
                    {on && entry.writeToolCount > 0 && (
                      <label className="flex items-center gap-2 text-[13px] text-[var(--foreground)] ml-6">
                        <input type="checkbox" checked={r.mcpWriteEnabled.includes(entry.id)}
                          onChange={e => toggleMcpWrite(entry.id, e.target.checked)}
                          className="accent-[var(--primary)]" />
                        Allow write actions
                        <span className="text-[11px] text-[var(--muted)]">(send/post/create)</span>
                      </label>
                    )}
                  </div>
                );
              })}
            </div>
            <p className="text-[11px] text-[var(--muted)] mt-2">Connect integrations per board in Board Settings → Integrations.</p>
          </div>
        )}

        <div className="flex gap-4 pt-1">
          <label className="flex items-center gap-2 text-sm text-[var(--foreground)]"><input type="checkbox" checked={r.permissions.canPushCode} onChange={e => setPerms({ canPushCode: e.target.checked })} className="accent-[var(--primary)]" /> Can push code</label>
          <label className="flex items-center gap-2 text-sm text-[var(--foreground)]"><input type="checkbox" checked={r.permissions.canDeploy} onChange={e => setPerms({ canDeploy: e.target.checked })} className="accent-[var(--primary)]" /> Can deploy</label>
        </div>
      </div>
    </Modal>
  );
}

function L({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">{label}</span><div className="mt-1">{children}</div></label>;
}
