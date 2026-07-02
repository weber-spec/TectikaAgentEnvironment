'use client';

import { useEffect, useRef, useState } from 'react';
import { api } from '@/lib/api';
import type { AgentRole, ClaudeAuthMode, ExecutionEngine, Connection, ConnectionCatalogEntry } from '@/lib/types';
import { colorFor } from '@/lib/palette';
import { Avatar, Button, Skeleton, EmptyState, Tag } from '@/components/ui/primitives';
import { Modal } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { BrandIcon } from '@/components/ui/brand-icons';
import { ModelSelect } from '@/components/ModelSelect';
import { toast } from '@/lib/toast';

/** Per-role sync state set after a successful upsert. */
type SyncState = { synced: boolean; error?: string | null };

/** Claude Code model choices (CLI `--model`). The default for a new Claude agent is Opus 4.8. */
const CLAUDE_MODELS: { id: string; label: string }[] = [
  { id: 'claude-opus-4-8', label: 'Claude Opus 4.8' },
  { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6' },
  { id: 'claude-haiku-4-5-20251001', label: 'Claude Haiku 4.5' },
];
const DEFAULT_CLAUDE_MODEL = 'claude-opus-4-8';
const CLAUDE_MODEL_IDS = CLAUDE_MODELS.map(m => m.id);

export default function AgentsPage() {
  const [roles, setRoles] = useState<AgentRole[] | null>(null);
  const [editing, setEditing] = useState<AgentRole | null>(null);
  const [syncStates, setSyncStates] = useState<Record<string, SyncState>>({});

  useEffect(() => { api.agentRoles.list().then(setRoles).catch(() => setRoles([])); }, []);

  const save = async (role: AgentRole, anthropicApiKey?: string) => {
    try {
      const result = await api.agentRoles.upsertFull(role, anthropicApiKey);
      const saved = result.role;
      setRoles(prev => { const list = prev ?? []; return list.some(r => r.id === saved.id) ? list.map(r => r.id === saved.id ? saved : r) : [...list, saved]; });
      setSyncStates(prev => ({ ...prev, [saved.id]: { synced: result.synced, error: result.error } }));
      setEditing(null);
      toast('Agent saved', 'success');
    } catch { toast('Could not save agent', 'error'); }
  };

  const newAgent = (): AgentRole => ({
    id: `role-${Date.now().toString(36)}`, tenantId: 'default', displayName: 'New Agent',
    systemPrompt: 'You are a helpful agent.', tools: [], connections: [],
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
  // Derive a synced indicator: explicit syncState from last save takes priority, otherwise fall back to
  // a passive indicator — foundryAgentId (Foundry) or apiKeySecretName (Claude Code, = key stored).
  const engine = role.executionEngine ?? 'Foundry';
  const passiveSynced = engine === 'ClaudeCode' ? !!role.apiKeySecretName : !!role.foundryAgentId;
  const isSynced = syncState ? syncState.synced : passiveSynced;
  const syncError = syncState?.error;
  const showSyncBadge = syncState !== undefined || passiveSynced;

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
        {role.connections?.map(c => <Tag key={c.connectionId} label={c.writeEnabled ? `${c.catalogId} ✎` : c.catalogId} hex="#a25ddc" />)}
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

function RoleEditor({ role, onSave, onClose }: { role: AgentRole; onSave: (r: AgentRole, apiKey?: string) => void | Promise<void>; onClose: () => void }) {
  const [r, setR] = useState<AgentRole>(role);
  // Anthropic API key (ClaudeCode engine) — kept local, never on the role object, sent only on save.
  const [apiKey, setApiKey] = useState('');
  const set = (p: Partial<AgentRole>) => setR(prev => ({ ...prev, ...p }));
  const setPerms = (p: Partial<typeof r.permissions>) =>
    setR(prev => ({ ...prev, permissions: { ...prev.permissions, ...p } }));

  const engine = r.executionEngine ?? 'Foundry';
  const isClaude = engine === 'ClaudeCode';
  const claudeAuth: ClaudeAuthMode = r.claudeAuth ?? 'ApiKey';
  const isOAuth = claudeAuth === 'OAuthToken';

  // Switching provider also swaps the model so the field never keeps a value from the other provider:
  // → Claude Code: default to Opus 4.8 unless it's already a Claude id. → Foundry: clear a Claude id to Default.
  const onEngineChange = (next: ExecutionEngine) => setR(prev => {
    let model = prev.modelOverride;
    if (next === 'ClaudeCode') model = model && CLAUDE_MODEL_IDS.includes(model) ? model : DEFAULT_CLAUDE_MODEL;
    else if (model && CLAUDE_MODEL_IDS.includes(model)) model = undefined;
    return { ...prev, executionEngine: next, modelOverride: model };
  });

  // Agent-tool connections come from the tenant registry (Connections page); the agent references specific
  // instances by id. The catalog map supplies each connection's read/write tool counts + brand icon.
  const [connections, setConnections] = useState<Connection[]>([]);
  const [catMap, setCatMap] = useState<Record<string, ConnectionCatalogEntry>>({});
  useEffect(() => {
    api.connections.list().then(cs => setConnections(cs.filter(c => c.category === 'agent-tool'))).catch(() => setConnections([]));
    api.connections.catalog().then(cat => setCatMap(Object.fromEntries(cat.map(c => [c.id, c])))).catch(() => setCatMap({}));
  }, []);

  const connRef = (id: string) => r.connections.find(c => c.connectionId === id);
  const toggleConn = (conn: Connection, on: boolean) =>
    setR(prev => ({
      ...prev,
      connections: on
        ? [...prev.connections.filter(c => c.connectionId !== conn.id),
           { connectionId: conn.id, catalogId: conn.catalogId, writeEnabled: false }]
        : prev.connections.filter(c => c.connectionId !== conn.id),
    }));
  const toggleConnWrite = (id: string, on: boolean) =>
    setR(prev => ({
      ...prev,
      connections: prev.connections.map(c => c.connectionId === id ? { ...c, writeEnabled: on } : c),
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
    try { await onSave(r, isClaude ? (apiKey || undefined) : undefined); }
    finally { savingRef.current = false; setSaving(false); }
  };
  return (
    <Modal open onClose={onClose} width={560} title={<span className="flex items-center gap-2"><Icon.robot size={18} /> {role.displayName === 'New Agent' ? 'New agent' : 'Edit agent'}</span>}
      footer={<><Button onClick={onClose} disabled={saving}>Cancel</Button><Button variant="primary" onClick={handleSave} disabled={saving}>{saving ? 'Saving…' : 'Save agent'}</Button></>}>
      <div className="flex flex-col gap-3">
        <L label="Display name"><input value={r.displayName} onChange={e => set({ displayName: e.target.value })} className="inp" /></L>
        <L label="System prompt"><textarea value={r.systemPrompt} onChange={e => set({ systemPrompt: e.target.value })} rows={4} className="inp resize-none" /></L>

        {/* Provider first — it determines which model list the field below shows. */}
        <L label="Provider">
          <select value={engine}
            onChange={e => onEngineChange(e.target.value as ExecutionEngine)}
            className="inp">
            <option value="Foundry">Azure Foundry</option>
            <option value="ClaudeCode">Claude Code</option>
          </select>
        </L>

        {/* Model — provider-dependent: Foundry's live catalog, or the Claude Code model list. */}
        <L label="Model">
          {isClaude ? (
            <select value={r.modelOverride ?? DEFAULT_CLAUDE_MODEL}
              onChange={e => set({ modelOverride: e.target.value })}
              className="inp" aria-label="Model">
              {CLAUDE_MODELS.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
            </select>
          ) : (
            <ModelSelect value={r.modelOverride} onChange={v => set({ modelOverride: v || undefined })} selectClassName="inp" />
          )}
        </L>

        {isClaude && (
          <>
            <L label="Authentication">
              <select value={claudeAuth}
                onChange={e => set({ claudeAuth: e.target.value as ClaudeAuthMode })}
                className="inp">
                <option value="ApiKey">API key (pay-as-you-go)</option>
                <option value="OAuthToken">Pro / Max subscription token</option>
              </select>
            </L>
            <L label={isOAuth ? 'Claude subscription token' : 'Anthropic API key'}>
              <input type="password" value={apiKey} onChange={e => setApiKey(e.target.value)}
                placeholder={r.apiKeySecretName ? 'Leave blank to keep existing' : (isOAuth ? 'sk-ant-oat…' : 'sk-ant-api…')}
                className="inp" autoComplete="off" />
              <span className="text-[11px] text-[var(--muted)] block mt-1">
                {isOAuth
                  ? <>Generate it with <code>claude setup-token</code> while logged into your Pro/Max account. Billed to your subscription, not the API.</>
                  : <>From platform.claude.com → API keys. Billed pay-as-you-go, separately from any subscription.</>}
                {' '}Stored securely in Azure Key Vault — never shown again.
              </span>
            </L>
          </>
        )}

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

        {/* Layer 3: Connections (agent tools) — reference tenant connections defined on the Connections page */}
        <div className="rounded-lg border border-[var(--border)] p-3">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold flex items-center gap-1.5 mb-2">
            <Icon.bolt size={13} /> Connections
          </span>
          {connections.length === 0 ? (
            <p className="text-[12px] text-[var(--muted)]">
              No tool connections yet. Create one on the <a href="/connections" className="underline text-[var(--primary)]">Connections</a> page, then enable it on a board.
            </p>
          ) : (
            <div className="flex flex-col gap-2">
              {connections.map(conn => {
                const ref = connRef(conn.id);
                const on = !!ref;
                const cat = catMap[conn.catalogId];
                const writable = (cat?.writeToolCount ?? 0) > 0;
                return (
                  <div key={conn.id} className="flex flex-col gap-1">
                    <label className="flex items-center gap-2 text-sm text-[var(--foreground)]">
                      <input type="checkbox" checked={on}
                        onChange={e => toggleConn(conn, e.target.checked)}
                        className="accent-[var(--primary)]" />
                      <BrandIcon name={cat?.iconKey ?? conn.catalogId} size={18} />
                      {conn.displayName}
                      {cat && <span className="text-[11px] text-[var(--muted)]">({cat.readToolCount} read · {cat.writeToolCount} write)</span>}
                    </label>
                    {on && writable && (
                      <label className="flex items-center gap-2 text-[13px] text-[var(--foreground)] ml-6">
                        <input type="checkbox" checked={ref?.writeEnabled ?? false}
                          onChange={e => toggleConnWrite(conn.id, e.target.checked)}
                          className="accent-[var(--primary)]" />
                        Allow write actions
                        <span className="text-[11px] text-[var(--muted)]">(send/post/create)</span>
                      </label>
                    )}
                  </div>
                );
              })}
              <p className="text-[11px] text-[var(--muted)] mt-1">The connection must also be enabled on the board (Board Settings → Integrations) for the agent to use it.</p>
            </div>
          )}
        </div>

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
