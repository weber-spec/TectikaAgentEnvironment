'use client';

import { api } from '@/lib/api';
import { useEffect, useState } from 'react';
import type { Board, AgentTask, WorkflowRun, AgentRole, UsageRollup, UsageTimePoint, AgentUsage } from '@/lib/types';
import { colorFor } from '@/lib/palette';
import { BarChart, type Datum } from '@/components/charts/Charts';
import { UsageChart } from '@/components/charts/UsageChart';
import { Skeleton, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { formatCurrency, formatCompact, formatDuration } from '@/lib/format';
import { ModelBreakdownTable } from '@/components/workspace/ModelBreakdownTable';
import { PricingTable } from '@/components/analytics/PricingTable';

const ALL = 'all';
const SERIES_DAYS = 365; // pulled once; the chart aggregates client-side into day/week/month

type Tab = 'overview' | 'pricing';

export default function AnalyticsPage() {
  const [boards, setBoards] = useState<Board[] | null>(null);
  const [scope, setScope] = useState<string>(''); // boardId or ALL; '' until boards resolve
  const [tab, setTab] = useState<Tab>('overview');

  useEffect(() => {
    api.boards.list()
      .then(bs => { setBoards(bs); setScope(prev => prev || (bs[0]?.id ?? ALL)); })
      .catch(() => { setBoards([]); setScope(ALL); });
  }, []);

  const scopeLabel = scope === ALL ? 'all boards in your workspace' : (boards?.find(b => b.id === scope)?.name ?? 'this project');

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-[var(--foreground)]">Analytics</h1>
          <p className="text-sm text-[var(--muted)] mt-0.5">
            {tab === 'overview' ? `Agent performance, throughput and cost for ${scopeLabel}.` : 'Model pricing catalog used to compute costs.'}
          </p>
        </div>
        {tab === 'overview' && boards && boards.length > 0 && (
          <label className="flex items-center gap-2 text-sm">
            <span className="text-[var(--muted)]">Project</span>
            <select
              value={scope}
              onChange={e => setScope(e.target.value)}
              className="rounded-md border border-[var(--border)] bg-[var(--background)] text-[var(--foreground)] px-2.5 py-1.5 text-sm"
            >
              {boards.map(b => <option key={b.id} value={b.id}>{b.name}</option>)}
              <option value={ALL}>All boards (workspace)</option>
            </select>
          </label>
        )}
      </div>

      <div className="px-8">
        <div className="flex items-center gap-1 border-b border-[var(--border)]">
          <TabButton active={tab === 'overview'} onClick={() => setTab('overview')}>Overview</TabButton>
          <TabButton active={tab === 'pricing'} onClick={() => setTab('pricing')}>Pricing</TabButton>
        </div>
      </div>

      <div className="px-8 py-6">
        {tab === 'pricing'
          ? <PricingTable />
          : (!boards || scope === ''
            ? <div className="grid grid-cols-3 gap-4">{[...Array(6)].map((_, i) => <Skeleton key={i} className="h-40" />)}</div>
            : <ScopedAnalytics key={scope} scope={scope} />)}
      </div>
    </div>
  );
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={`px-4 py-2.5 text-sm font-medium -mb-px border-b-2 transition-colors ${active ? 'border-[var(--primary,#0073ea)] text-[var(--foreground)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--foreground)]'}`}
    >
      {children}
    </button>
  );
}

type ScopedData = { tasks: AgentTask[]; runs: WorkflowRun[]; roles: AgentRole[]; usage: UsageRollup | null; series: UsageTimePoint[]; byAgent: AgentUsage[] };

function ScopedAnalytics({ scope }: { scope: string }) {
  const [d, setD] = useState<ScopedData | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      const roles = await api.agentRoles.list().catch(() => [] as AgentRole[]);
      let tasks: AgentTask[];
      if (scope === ALL) {
        const boards = await api.boards.list().catch(() => [] as Board[]);
        tasks = (await Promise.all(boards.map(b => api.tasks.list(b.id).catch(() => [])))).flat();
      } else {
        tasks = await api.tasks.list(scope).catch(() => [] as AgentTask[]);
      }
      const runs = (await Promise.all(
        tasks.filter(t => t.workflowRunId).map(t => api.runs.get(t.id, t.workflowRunId!).catch(() => null)),
      )).filter((r): r is WorkflowRun => !!r);
      const [usage, series, byAgent] = await Promise.all([
        scope === ALL ? api.usage.project().catch(() => null) : api.usage.board(scope).catch(() => null),
        scope === ALL ? api.usage.projectTimeseries(SERIES_DAYS).catch(() => []) : api.usage.boardTimeseries(scope, SERIES_DAYS).catch(() => []),
        scope === ALL ? api.usage.byAgentProject(SERIES_DAYS).catch(() => []) : api.usage.byAgentBoard(scope, SERIES_DAYS).catch(() => []),
      ]);
      if (!cancelled) setD({ tasks, runs, roles, usage, series, byAgent });
    };
    load();
    const iv = setInterval(load, 7000); // live: refresh KPIs/charts as runs progress + after /clear
    return () => { cancelled = true; clearInterval(iv); };
  }, [scope]);

  if (!d) return <div className="grid grid-cols-3 gap-4">{[...Array(6)].map((_, i) => <Skeleton key={i} className="h-40" />)}</div>;
  return <Body {...d} />;
}

function Body({ tasks, runs, roles, usage, series, byAgent }: ScopedData) {
  const totalTokens = usage?.lifetime.tokens.total ?? runs.reduce((s, r) => s + r.totalTokens, 0);
  const totalCost = usage?.lifetime.costUsd ?? runs.reduce((s, r) => s + r.estimatedCostUsd, 0);
  // Average wall-clock run duration (completed runs). Steerable runs track time at the run level, not per step.
  const runDurations = runs.filter(r => r.completedAt).map(r => new Date(r.completedAt!).getTime() - new Date(r.startedAt).getTime()).filter(ms => ms > 0);
  const avgDuration = runDurations.length ? runDurations.reduce((s, x) => s + x, 0) / runDurations.length : 0;

  // Tokens per agent — ledger truth (UsageEvent), keyed by agentRoleId.
  const tokensByRole = new Map<string, number>(byAgent.map(a => [a.agentRoleId, a.tokens.total]));
  const tokenData: Datum[] = byAgent
    .filter(a => a.tokens.total > 0)
    .map(a => ({ label: roles.find(r => r.id === a.agentRoleId)?.displayName ?? a.agentRoleName, hex: colorFor(a.agentRoleId), value: a.tokens.total }));

  // completed per agent (from task status)
  const completedByAgent = new Map<string, number>();
  tasks.filter(t => t.status === 'Done' && t.assignee.type === 'Agent').forEach(t => completedByAgent.set(t.assignee.id, (completedByAgent.get(t.assignee.id) ?? 0) + 1));

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-4 gap-4">
        <Kpi icon={<Icon.bolt size={18} />} label="Total tokens" value={formatCompact(totalTokens)} color="#a25ddc" />
        <Kpi icon={<Icon.bolt size={18} />} label="Estimated cost" value={formatCurrency(totalCost)} color="#0073ea" />
        <Kpi icon={<Icon.refresh size={18} />} label="Agent runs" value={String(runs.length)} color="#00c875" />
        <Kpi icon={<Icon.clock size={18} />} label="Avg run time" value={formatDuration(avgDuration) || '—'} color="#fdab3d" />
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Card title="Token usage over time">
          <UsageChart series={series} />
        </Card>
        <Card title="Token usage by agent">{tokenData.length ? <BarChart data={tokenData} horizontal height={200} /> : <p className="text-sm text-[var(--muted)]">No usage recorded yet.</p>}</Card>
      </div>

      <Card title="Agent leaderboard">
        <div className="flex flex-col">
          <div className="grid grid-cols-12 text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold py-2 border-b border-[var(--border)]">
            <span className="col-span-5">Agent</span><span className="col-span-2 text-right">Completed</span><span className="col-span-3 text-right">Tokens</span><span className="col-span-2 text-right">Model</span>
          </div>
          {roles.map(r => (
            <div key={r.id} className="grid grid-cols-12 items-center py-2 border-b border-[var(--border)] last:border-0 text-sm">
              <div className="col-span-5 flex items-center gap-2 min-w-0"><Avatar person={{ id: r.id, name: r.displayName, kind: 'Agent', hex: colorFor(r.id) }} size={26} /><span className="text-[var(--foreground)] truncate">{r.displayName}</span></div>
              <span className="col-span-2 text-right text-[var(--foreground)] font-medium">{completedByAgent.get(r.id) ?? 0}</span>
              <span className="col-span-3 text-right text-[var(--muted)]">{formatCompact(tokensByRole.get(r.id) ?? 0)}</span>
              <span className="col-span-2 text-right text-[11px] text-[var(--muted)] truncate">{r.modelOverride ?? '—'}</span>
            </div>
          ))}
        </div>
      </Card>

      <Card title="Cost by model">
        <ModelBreakdownTable usage={usage} />
      </Card>
    </div>
  );
}

function Kpi({ icon, label, value, color }: { icon: React.ReactNode; label: string; value: string; color: string }) {
  return (
    <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4">
      <div className="flex items-center gap-2 mb-2"><span className="w-8 h-8 rounded-lg flex items-center justify-center" style={{ background: `${color}22`, color }}>{icon}</span><span className="text-xs text-[var(--muted)]">{label}</span></div>
      <div className="text-2xl font-bold text-[var(--foreground)]">{value}</div>
    </div>
  );
}
function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4"><h3 className="text-sm font-semibold text-[var(--foreground)] mb-3">{title}</h3>{children}</div>;
}
