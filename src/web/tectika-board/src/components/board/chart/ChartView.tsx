'use client';

import React from 'react';
import { useBoard } from '@/lib/board-context';
import { cellGroup, cellNumber, KIND_META } from '@/lib/columns';
import type { ChartConfig } from '@/lib/types';
import { BarChart, PieChart, LineChart, type Datum } from '@/components/charts/Charts';

export function ChartView() {
  const { visibleTasks, columns, cellContext, activeView, updateActiveView } = useBoard();
  const chart: ChartConfig = activeView.chart ?? { type: 'bar', groupBy: 'status', metric: 'count' };
  const groupCol = columns.find(c => c.id === chart.groupBy) ?? columns[1];
  const metricCol = columns.find(c => c.id === chart.metricColumnId);

  // build data
  const buckets = new Map<string, Datum>();
  const acc = new Map<string, number[]>();
  for (const t of visibleTasks) {
    const g = cellGroup(t, groupCol, cellContext);
    if (!buckets.has(g.key)) { buckets.set(g.key, { label: g.label, hex: g.hex, value: 0 }); acc.set(g.key, []); }
    if (chart.metric === 'count') buckets.get(g.key)!.value += 1;
    else if (metricCol) { const n = cellNumber(t, metricCol, cellContext); if (n != null) acc.get(g.key)!.push(n); }
  }
  if (chart.metric !== 'count' && metricCol) {
    for (const [k, nums] of acc) {
      const sum = nums.reduce((a, b) => a + b, 0);
      buckets.get(k)!.value = chart.metric === 'avg' ? (nums.length ? Math.round(sum / nums.length) : 0) : sum;
    }
  }
  const data = [...buckets.values()].sort((a, b) => b.value - a.value);

  const groupable = columns.filter(c => ['label'].includes(KIND_META[c.kind].domain) || ['status', 'priority', 'people', 'dropdown', 'trigger'].includes(c.kind));
  const numeric = columns.filter(c => KIND_META[c.kind].domain === 'number');
  const set = (p: Partial<ChartConfig>) => updateActiveView({ chart: { ...chart, ...p } });

  return (
    <div className="h-full overflow-auto p-6">
      <div className="flex items-center gap-2 mb-5 flex-wrap">
        <Sel value={chart.type} onChange={v => set({ type: v as ChartConfig['type'] })} options={[['column', 'Column'], ['bar', 'Bar'], ['pie', 'Pie'], ['donut', 'Donut'], ['line', 'Line']]} label="Chart" />
        <Sel value={chart.groupBy} onChange={v => set({ groupBy: v })} options={groupable.map(c => [c.id, c.title] as [string, string])} label="Group by" />
        <Sel value={chart.metric} onChange={v => set({ metric: v as ChartConfig['metric'] })} options={[['count', 'Count of items'], ['sum', 'Sum'], ['avg', 'Average']]} label="Value" />
        {chart.metric !== 'count' && (
          <Sel value={chart.metricColumnId ?? ''} onChange={v => set({ metricColumnId: v })} options={numeric.map(c => [c.id, c.title] as [string, string])} label="of" />
        )}
      </div>
      <div className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-6 min-h-[320px] flex items-center justify-center">
        {data.every(d => d.value === 0) ? (
          <p className="text-sm text-[var(--muted)]">No data to chart yet.</p>
        ) : chart.type === 'pie' || chart.type === 'donut' ? (
          <PieChart data={data} donut={chart.type === 'donut'} size={260} />
        ) : chart.type === 'line' ? (
          <LineChart data={data} />
        ) : (
          <div className="w-full"><BarChart data={data} horizontal={chart.type === 'bar'} height={320} /></div>
        )}
      </div>
    </div>
  );
}

function Sel({ value, onChange, options, label }: { value: string; onChange: (v: string) => void; options: [string, string][]; label: string }) {
  return (
    <label className="flex items-center gap-1.5 text-sm">
      <span className="text-[var(--muted)]">{label}</span>
      <select value={value} onChange={e => onChange(e.target.value)} className="bg-[var(--surface)] border border-[var(--border)] rounded-md px-2 py-1.5 text-[var(--foreground)] outline-none">
        {options.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
      </select>
    </label>
  );
}
