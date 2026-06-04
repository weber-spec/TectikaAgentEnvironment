'use client';

import React from 'react';
import { useBoard } from '@/lib/board-context';
import type { FilterRule, FilterOperator, ColumnDef } from '@/lib/types';
import { KIND_META } from '@/lib/columns';
import { STATUS_CONFIG, STATUS_ORDER, PRIORITY_CONFIG, PRIORITY_ORDER } from '@/lib/palette';
import { Popover } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { uid } from '@/lib/collaboration';

const OPERATORS: Record<string, { value: FilterOperator; label: string }[]> = {
  label: [{ value: 'is', label: 'is' }, { value: 'isNot', label: 'is not' }, { value: 'isEmpty', label: 'is empty' }, { value: 'isNotEmpty', label: 'is not empty' }],
  text: [{ value: 'contains', label: 'contains' }, { value: 'notContains', label: 'does not contain' }, { value: 'isEmpty', label: 'is empty' }, { value: 'isNotEmpty', label: 'is not empty' }],
  number: [{ value: 'gt', label: '>' }, { value: 'lt', label: '<' }, { value: 'gte', label: '≥' }, { value: 'lte', label: '≤' }],
  date: [{ value: 'before', label: 'before' }, { value: 'after', label: 'after' }, { value: 'isEmpty', label: 'is empty' }, { value: 'isNotEmpty', label: 'is not empty' }],
  people: [{ value: 'is', label: 'is' }, { value: 'isNot', label: 'is not' }],
  meta: [{ value: 'contains', label: 'contains' }],
  bool: [{ value: 'is', label: 'is' }],
};

function opsFor(col: ColumnDef) { return OPERATORS[KIND_META[col.kind].domain] ?? OPERATORS.text; }

export function FilterBuilder({ anchorRef, open, onClose }: { anchorRef: React.RefObject<HTMLElement | null>; open: boolean; onClose: () => void }) {
  const { columns, activeView, setFilter, people } = useBoard();
  const filter = activeView.filter ?? { conjunction: 'and' as const, rules: [] };

  const update = (rules: FilterRule[], conjunction = filter.conjunction) => setFilter(rules.length ? { conjunction, rules } : undefined);
  const addRule = () => update([...filter.rules, { id: uid('f'), columnId: columns[1]?.id ?? columns[0].id, operator: 'is' }]);
  const setRule = (id: string, patch: Partial<FilterRule>) => update(filter.rules.map(r => r.id === id ? { ...r, ...patch } : r));
  const removeRule = (id: string) => update(filter.rules.filter(r => r.id !== id));

  return (
    <Popover anchorRef={anchorRef} open={open} onClose={onClose} width={460} className="p-3">
      <div className="flex items-center justify-between mb-2">
        <span className="text-sm font-semibold text-[var(--foreground)]">Advanced filter</span>
        {filter.rules.length > 0 && <button onClick={() => setFilter(undefined)} className="text-xs text-[#e2445c] hover:underline">Clear all</button>}
      </div>
      {filter.rules.length === 0 && <p className="text-xs text-[var(--muted)] mb-2">Show items that match all conditions.</p>}
      <div className="flex flex-col gap-2">
        {filter.rules.map((rule, i) => {
          const col = columns.find(c => c.id === rule.columnId) ?? columns[0];
          const ops = opsFor(col);
          const needsValue = !['isEmpty', 'isNotEmpty'].includes(rule.operator);
          return (
            <div key={rule.id} className="flex items-center gap-1.5">
              <span className="w-12 shrink-0 text-xs text-[var(--muted)] text-right">
                {i === 0 ? 'Where' : (
                  <select value={filter.conjunction} onChange={e => update(filter.rules, e.target.value as 'and' | 'or')} className="bg-[var(--surface)] rounded px-1 py-0.5 text-[var(--foreground)] outline-none">
                    <option value="and">and</option><option value="or">or</option>
                  </select>
                )}
              </span>
              <select value={rule.columnId} onChange={e => { const nc = columns.find(c => c.id === e.target.value)!; setRule(rule.id, { columnId: e.target.value, operator: opsFor(nc)[0].value, value: undefined }); }}
                className="flex-1 min-w-0 text-sm bg-[var(--surface)] rounded px-2 py-1.5 outline-none text-[var(--foreground)] border border-[var(--border)]">
                {columns.map(c => <option key={c.id} value={c.id}>{c.title}</option>)}
              </select>
              <select value={rule.operator} onChange={e => setRule(rule.id, { operator: e.target.value as FilterOperator })}
                className="text-sm bg-[var(--surface)] rounded px-2 py-1.5 outline-none text-[var(--foreground)] border border-[var(--border)]">
                {ops.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select>
              {needsValue && <ValueInput col={col} rule={rule} people={people} onChange={v => setRule(rule.id, { value: v })} />}
              <button onClick={() => removeRule(rule.id)} className="shrink-0 w-7 h-7 flex items-center justify-center text-[var(--muted)] hover:text-[#e2445c] rounded"><Icon.x size={14} /></button>
            </div>
          );
        })}
      </div>
      <button onClick={addRule} className="mt-3 flex items-center gap-1.5 text-sm text-[var(--primary)] font-medium hover:underline"><Icon.plus size={14} /> Add condition</button>
    </Popover>
  );
}

function ValueInput({ col, rule, people, onChange }: { col: ColumnDef; rule: FilterRule; people: { id: string; name: string }[]; onChange: (v: string) => void }) {
  const cls = 'flex-1 min-w-0 text-sm bg-[var(--surface)] rounded px-2 py-1.5 outline-none text-[var(--foreground)] border border-[var(--border)]';
  const val = typeof rule.value === 'string' ? rule.value : '';
  if (col.kind === 'status') return <select value={val} onChange={e => onChange(e.target.value)} className={cls}><option value="">—</option>{STATUS_ORDER.map(s => <option key={s} value={STATUS_CONFIG[s].label}>{STATUS_CONFIG[s].label}</option>)}</select>;
  if (col.kind === 'priority') return <select value={val} onChange={e => onChange(e.target.value)} className={cls}><option value="">—</option>{PRIORITY_ORDER.map(s => <option key={s} value={PRIORITY_CONFIG[s].label}>{PRIORITY_CONFIG[s].label}</option>)}</select>;
  if (col.kind === 'people') return <select value={val} onChange={e => onChange(e.target.value)} className={cls}><option value="">—</option>{people.map(p => <option key={p.id} value={p.name}>{p.name}</option>)}</select>;
  if (KIND_META[col.kind].domain === 'number') return <input type="number" value={val} onChange={e => onChange(e.target.value)} className={cls} placeholder="value" />;
  if (KIND_META[col.kind].domain === 'date') return <input type="date" value={val} onChange={e => onChange(e.target.value)} className={cls} />;
  return <input value={val} onChange={e => onChange(e.target.value)} className={cls} placeholder="value" />;
}
