'use client';

import { api } from '@/lib/api';
import { useEffect, useState } from 'react';
import type { Board, AgentTask, WorkflowRun, AgentRole, UsageRollup, UsageTimePoint } from '@/lib/types';
import { colorFor } from '@/lib/palette';
import { BarChart, LineChart, type Datum } from '@/components/charts/Charts';
import { Skeleton, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { formatCurrency, formatCompact, formatDuration, displayName } from '@/lib/format';
import { ModelBreakdownTable } from '@/components/workspace/ModelBreakdownTable';

const ALL = 'all';

export default function AnalyticsPage() {
  const [boards, setBoards] = useState<Board[] | null>(null);
  const [scope, setScope] = useState<string>(''); // boardId or ALL; '' until boards resolve

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
          <p className="text-sm text-[var(--muted)] mt-0.5">Agent performance, throughput and cost for {scopeLabel}.</p>
        </div>
        {boards && boards.length > 0 && (
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
      <div className="px-8 pb-8">
        {!boards || scope === ''
          ? <div className="grid grid-cols-3 gap-4">{[...Array(6)].map((_, i) => <Skeleton key={i} className="h-40" />)}</div>
          : <ScopedAnalytics key={scope} scope={scope} />}
      </div>
    </div>
  );
}

type ScopedData = { tasks: AgentTask[]; runs: WorkflowRun[]; roles: AgentRole[]; usage: UsageRollup | null; series: UsageTimePoint[] };

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
      const [usage, series] = await Promise.all([
        scope === ALL ? api.usage.project().catch(() => null) : api.usage.board(scope).catch(() => null),
        scope === ALL ? api.usage.projectTimeseries(14).catch(() => []) : api.usage.boardTimeseries(scope, 14).catch(() => []),
      ]);
      if (!cancelled) setD({ tasks, runs, roles, usage, series });
    };
    load();
    const iv = setInterval(load, 7000); // live: refresh KPIs/charts as runs progress + after /clear
    return () => { cancelled = true; clearInterval(iv); };
  }, [scope]);

  if (!d) return <div className="grid grid-cols-3 gap-4">{[...Array(6)].map((_, i) => <Skeleton key={i} className="h-40" />)}</div>;
  return <Body {...d} />;
}

function Body({ tasks, runs, roles, usage, series }: ScopedData) {
  const totalTokens = usage?.lifetime.tokens.total ?? runs.reduce((s, r) => s + r.totalTokens, 0);
  const totalCost = usage?.lifetime.costUsd ?? runs.reduce((s, r) => s + r.estimatedCostUsd, 0);
  const avgDuration = runs.flatMap(r => r.steps).filter(s => s.durationMs).reduce((s, x, _, arr) => s + x.durationMs / arr.length, 0);

  // tokens per agent role (via tasks → roles)
  const tokensByRole = new Map<string, number>();
  runs.forEach(r => { const task = tasks.find(t => t.id === r.taskId); if (task?.assignee.type === 'Agent') tokensByRole.set(task.assignee.id, (tokensByRole.get(task.assignee.id) ?? 0) + r.totalTokens); });
  const tokenData: Datum[] = [...tokensByRole.entries()].map(([id, v]) => ({ label: roles.find(r => r.id === id)?.displayName ?? displayName(id), hex: colorFor(id), value: v }));

  // completed per agent
  const completedByAgent = new Map<string, number>();
  tasks.filter(t => t.status === 'Done' && t.assignee.type === 'Agent').forEach(t => completedByAgent.set(t.assignee.id, (completedByAgent.get(t.assignee.id) ?? 0) + 1));

  // REAL token usage per day from the usage ledger (zero-filled by the API)
  const lineData: Datum[] = series.map(p => ({ label: p.date.slice(5), hex: '#0073ea', value: p.tokens }));
  const hasSeries = series.some(p => p.tokens > 0);

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-4 gap-4">
        <Kpi icon={<Icon.bolt size={18} />} label="Total tokens" value={formatCompact(totalTokens)} color="#a25ddc" />
        <Kpi icon={<Icon.bolt size={18} />} label="Estimated cost" value={formatCurrency(totalCost)} color="#0073ea" />
        <Kpi icon={<Icon.refresh size={18} />} label="Agent runs" value={String(runs.length)} color="#00c875" />
        <Kpi icon={<Icon.clock size={18} />} label="Avg step time" value={formatDuration(avgDuration) || '—'} color="#fdab3d" />
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Card title="Tokens / day (last 14 days)">
          {hasSeries ? <LineChart data={lineData} /> : <p className="text-sm text-[var(--muted)]">No usage recorded in this period yet.</p>}
        </Card>
        <Card title="Token usage by agent">{tokenData.length ? <BarChart data={tokenData} horizontal height={200} /> : <p className="text-sm text-[var(--muted)]">No runs recorded yet.</p>}</Card>
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
