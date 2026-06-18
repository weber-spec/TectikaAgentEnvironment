'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { api } from '@/lib/api';
import type { Board, AgentTask, WorkflowRun, AgentRole, UsageRollup } from '@/lib/types';
import { STATUS_CONFIG, STATUS_ORDER, PRIORITY_CONFIG, PRIORITY_ORDER, colorFor } from '@/lib/palette';
import { BarChart, PieChart, type Datum } from '@/components/charts/Charts';
import { Skeleton, Avatar } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { formatCurrency, formatCompact, displayName, formatDateShort, daysUntil } from '@/lib/format';
import { ModelBreakdownTable } from '@/components/workspace/ModelBreakdownTable';

interface Data { boards: Board[]; tasks: AgentTask[]; runs: WorkflowRun[]; roles: AgentRole[] }

function useWorkspaceData() {
  const [data, setData] = useState<Data | null>(null);
  useEffect(() => {
    (async () => {
      const [boards, roles] = await Promise.all([api.boards.list().catch(() => []), api.agentRoles.list().catch(() => [])]);
      const taskLists = await Promise.all(boards.map(b => api.tasks.list(b.id).catch(() => [])));
      const tasks = taskLists.flat();
      const runResults = await Promise.all(tasks.filter(t => t.workflowRunId).map(t => api.runs.get(t.id, t.workflowRunId!).catch(() => null)));
      setData({ boards, tasks, runs: runResults.filter((r): r is WorkflowRun => !!r), roles });
    })();
  }, []);
  return data;
}

export default function DashboardsPage() {
  const data = useWorkspaceData();
  const [usage, setUsage] = useState<UsageRollup | null>(null);
  useEffect(() => { api.usage.project().then(setUsage).catch(() => {}); }, []);
  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5">
        <h1 className="text-2xl font-bold text-[var(--foreground)]">Dashboards</h1>
        <p className="text-sm text-[var(--muted)] mt-0.5">A live overview across all boards in your workspace.</p>
      </div>
      <div className="px-8 pb-8">
        {!data ? <DashSkeleton /> : <Dashboard data={data} usage={usage} />}
      </div>
    </div>
  );
}

function DashSkeleton() {
  return <div className="grid grid-cols-4 gap-4">{[...Array(8)].map((_, i) => <Skeleton key={i} className="h-28" style={{ gridColumn: i < 4 ? 'span 1' : 'span 2', height: i < 4 ? 96 : 280 }} />)}</div>;
}

function Dashboard({ data, usage }: { data: Data; usage: UsageRollup | null }) {
  const { boards, tasks, roles } = data;
  const nameFor = (id: string) => roles.find(r => r.id === id)?.displayName ?? displayName(id);
  const done = tasks.filter(t => t.status === 'Done').length;
  const inProgress = tasks.filter(t => t.status === 'InProgress').length;
  const overdue = tasks.filter(t => { const d = daysUntil(t.dueAt); return d != null && d < 0 && t.status !== 'Done'; }).length;
  const tokens = usage?.lifetime.tokens.total ?? 0;
  const cost = usage?.lifetime.costUsd ?? 0;

  const statusData: Datum[] = STATUS_ORDER.map(s => ({ label: STATUS_CONFIG[s].label, hex: STATUS_CONFIG[s].hex, value: tasks.filter(t => t.status === s).length })).filter(d => d.value > 0);
  const prioData: Datum[] = PRIORITY_ORDER.map(p => ({ label: PRIORITY_CONFIG[p].label, hex: PRIORITY_CONFIG[p].hex, value: tasks.filter(t => t.priority === p).length }));
  const boardData: Datum[] = boards.map(b => ({ label: b.name, hex: colorFor(b.name), value: tasks.filter(t => t.boardId === b.id).length }));

  const workload = new Map<string, number>();
  tasks.filter(t => t.status !== 'Done').forEach(t => workload.set(t.assignee.id, (workload.get(t.assignee.id) ?? 0) + 1));
  const workloadData: Datum[] = [...workload.entries()].map(([id, v]) => ({ label: nameFor(id), hex: colorFor(id), value: v })).sort((a, b) => b.value - a.value).slice(0, 8);

  const upcoming = tasks.filter(t => t.dueAt && t.status !== 'Done').sort((a, b) => +new Date(a.dueAt!) - +new Date(b.dueAt!)).slice(0, 8);

  return (
    <div className="grid grid-cols-4 gap-4">
      <Kpi icon={<Icon.board size={18} />} label="Total items" value={String(tasks.length)} sub={`${boards.length} boards`} color="#0073ea" />
      <Kpi icon={<Icon.bolt size={18} />} label="In progress" value={String(inProgress)} sub={`${done} done`} color="#fdab3d" />
      <Kpi icon={<Icon.warning size={18} />} label="Overdue" value={String(overdue)} sub={overdue ? 'needs attention' : 'all on track'} color={overdue ? '#e2445c' : '#00c875'} />
      <Kpi icon={<Icon.bolt size={18} />} label="Agent spend" value={formatCurrency(cost)} sub={`${formatCompact(tokens)} tokens`} color="#a25ddc" />

      <Widget title="Status distribution" span={2}><PieChart data={statusData} donut size={200} /></Widget>
      <Widget title="Items by board" span={2}><BarChart data={boardData} horizontal height={200} /></Widget>

      <Widget title="Priority breakdown" span={2}><BarChart data={prioData} height={220} /></Widget>
      <Widget title="Workload (open items)" span={2}><BarChart data={workloadData} horizontal height={220} /></Widget>

      <Widget title="Completion by board" span={2}>
        <div className="flex flex-col gap-3">
          {boards.map(b => {
            const bt = tasks.filter(t => t.boardId === b.id);
            const counts = new Map<string, number>(); bt.forEach(t => counts.set(t.status, (counts.get(t.status) ?? 0) + 1));
            const pct = bt.length ? Math.round((counts.get('Done') ?? 0) / bt.length * 100) : 0;
            return (
              <Link key={b.id} href={`/boards/${b.id}`} className="block group">
                <div className="flex justify-between text-xs mb-1"><span className="text-[var(--foreground)] font-medium group-hover:text-[var(--primary)]">{b.name}</span><span className="text-[var(--muted)]">{pct}%</span></div>
                <div className="flex h-2.5 rounded-full overflow-hidden bg-[var(--surface-2)]">
                  {[...counts.entries()].map(([s, n]) => <div key={s} style={{ flex: n, background: STATUS_CONFIG[s as keyof typeof STATUS_CONFIG]?.hex }} />)}
                </div>
              </Link>
            );
          })}
        </div>
      </Widget>

      <Widget title="Upcoming deadlines" span={2}>
        <div className="flex flex-col">
          {upcoming.length === 0 && <p className="text-sm text-[var(--muted)]">Nothing due soon.</p>}
          {upcoming.map(t => {
            const dd = daysUntil(t.dueAt); const overdueItem = dd != null && dd < 0;
            return (
              <Link key={t.id} href={`/boards/${t.boardId}`} className="flex items-center gap-2 py-1.5 border-b border-[var(--border)] last:border-0 group">
                <span className="w-2 h-2 rounded-full shrink-0" style={{ background: STATUS_CONFIG[t.status].hex }} />
                <span className="flex-1 text-sm text-[var(--foreground)] truncate group-hover:text-[var(--primary)]">{t.title}</span>
                <Avatar name={displayName(t.assignee.id)} hex={colorFor(t.assignee.id)} size={20} />
                <span className={`text-xs w-16 text-right ${overdueItem ? 'text-[#e2445c]' : 'text-[var(--muted)]'}`}>{formatDateShort(t.dueAt)}</span>
              </Link>
            );
          })}
        </div>
      </Widget>

      <Widget title="Cost by model" span={4}>
        <ModelBreakdownTable usage={usage} />
      </Widget>
    </div>
  );
}

function Kpi({ icon, label, value, sub, color }: { icon: React.ReactNode; label: string; value: string; sub: string; color: string }) {
  return (
    <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4">
      <div className="flex items-center gap-2 mb-2"><span className="w-8 h-8 rounded-lg flex items-center justify-center" style={{ background: `${color}22`, color }}>{icon}</span><span className="text-xs text-[var(--muted)]">{label}</span></div>
      <div className="text-2xl font-bold text-[var(--foreground)]">{value}</div>
      <div className="text-[11px] text-[var(--muted)]">{sub}</div>
    </div>
  );
}

function Widget({ title, span, children }: { title: string; span: number; children: React.ReactNode }) {
  return (
    <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 flex flex-col" style={{ gridColumn: `span ${span}` }}>
      <h3 className="text-sm font-semibold text-[var(--foreground)] mb-3">{title}</h3>
      <div className="flex-1 flex items-center justify-center">{children}</div>
    </div>
  );
}
