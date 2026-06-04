// Pure filter / sort / group engine over tasks, driven by ColumnDefs and the
// active view's FilterGroup / SortRule[] / groupBy.

import type { AgentTask, ColumnDef, FilterGroup, FilterRule, SortRule } from './types';
import { cellNumber, cellSortKey, cellText, cellEmpty, cellGroup, type CellContext } from './columns';

function colById(columns: ColumnDef[], id: string): ColumnDef | undefined {
  return columns.find(c => c.id === id);
}

function matchRule(task: AgentTask, rule: FilterRule, columns: ColumnDef[], ctx: CellContext): boolean {
  const col = colById(columns, rule.columnId);
  if (!col) return true;
  const text = cellText(task, col, ctx).toLowerCase();
  const num = cellNumber(task, col, ctx);
  const val = rule.value;
  const valStr = (typeof val === 'string' ? val : Array.isArray(val) ? '' : String(val ?? '')).toLowerCase();

  switch (rule.operator) {
    case 'is': return text === valStr;
    case 'isNot': return text !== valStr;
    case 'isEmpty': return cellEmpty(task, col, ctx);
    case 'isNotEmpty': return !cellEmpty(task, col, ctx);
    case 'contains': return text.includes(valStr);
    case 'notContains': return !text.includes(valStr);
    case 'anyOf': return Array.isArray(val) ? val.map(v => v.toLowerCase()).includes(text) : false;
    case 'gt': return num != null && val != null && num > Number(val);
    case 'lt': return num != null && val != null && num < Number(val);
    case 'gte': return num != null && val != null && num >= Number(val);
    case 'lte': return num != null && val != null && num <= Number(val);
    case 'before': return num != null && val != null && num < new Date(String(val)).getTime();
    case 'after': return num != null && val != null && num > new Date(String(val)).getTime();
    default: return true;
  }
}

export function applyFilter(tasks: AgentTask[], filter: FilterGroup | undefined, columns: ColumnDef[], ctx: CellContext): AgentTask[] {
  if (!filter || filter.rules.length === 0) return tasks;
  return tasks.filter(task => {
    const results = filter.rules.map(r => matchRule(task, r, columns, ctx));
    return filter.conjunction === 'and' ? results.every(Boolean) : results.some(Boolean);
  });
}

export function applySearch(tasks: AgentTask[], query: string, columns: ColumnDef[], ctx: CellContext): AgentTask[] {
  const q = query.trim().toLowerCase();
  if (!q) return tasks;
  return tasks.filter(task =>
    columns.some(col => cellText(task, col, ctx).toLowerCase().includes(q)) ||
    task.description.toLowerCase().includes(q),
  );
}

export function applySort(tasks: AgentTask[], sorts: SortRule[] | undefined, columns: ColumnDef[], ctx: CellContext): AgentTask[] {
  if (!sorts || sorts.length === 0) return tasks;
  const out = [...tasks];
  out.sort((a, b) => {
    for (const s of sorts) {
      const col = colById(columns, s.columnId);
      if (!col) continue;
      const ka = cellSortKey(a, col, ctx);
      const kb = cellSortKey(b, col, ctx);
      let cmp = 0;
      if (typeof ka === 'number' && typeof kb === 'number') cmp = ka - kb;
      else cmp = String(ka).localeCompare(String(kb));
      if (cmp !== 0) return s.direction === 'asc' ? cmp : -cmp;
    }
    return 0;
  });
  return out;
}

export interface TaskGroup {
  key: string;
  label: string;
  hex: string;
  tasks: AgentTask[];
}

export function groupTasks(tasks: AgentTask[], groupById: string | undefined, columns: ColumnDef[], ctx: CellContext): TaskGroup[] {
  const col = groupById ? colById(columns, groupById) : undefined;
  if (!col) return [{ key: '__all__', label: 'All items', hex: '#c4c4c4', tasks }];

  const groups = new Map<string, TaskGroup>();
  for (const task of tasks) {
    const g = cellGroup(task, col, ctx);
    const existing = groups.get(g.key);
    if (existing) existing.tasks.push(task);
    else groups.set(g.key, { key: g.key, label: g.label, hex: g.hex, tasks: [task] });
  }
  return [...groups.values()];
}
