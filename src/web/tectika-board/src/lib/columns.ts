// Column system — the registry of Monday.com-style column types, their default
// configuration, and pure value accessors used by grouping, sorting, filtering,
// aggregation and search. Cell rendering/editing lives in the table components.

import type {
  AgentTask, AgentRole, WorkflowRun, ColumnDef, ColumnKind, ColumnAggregation, Person,
} from './types';
import { STATUS_CONFIG, PRIORITY_CONFIG, colorFor } from './palette';
import { displayName, formatDate } from './format';

export interface CellContext {
  roles: AgentRole[];
  peopleById: Record<string, Person>;
  runsById: Record<string, WorkflowRun>;
  /** customCells[taskId][columnId] = raw string value. */
  customCells: Record<string, Record<string, string>>;
  tasksById: Record<string, AgentTask>;
  /** Dependency-edge upstream/downstream ids per taskId (derived from TaskEdge[]). */
  upstreamIds: Record<string, string[]>;
  downstreamIds: Record<string, string[]>;
}

export interface KindMeta {
  label: string;
  defaultWidth: number;
  defaultAgg: ColumnAggregation;
  /** value domain — drives editors, aggregation menus and chart eligibility. */
  domain: 'text' | 'label' | 'people' | 'date' | 'number' | 'bool' | 'meta';
}

export const KIND_META: Record<ColumnKind, KindMeta> = {
  title:       { label: 'Item',         defaultWidth: 280, defaultAgg: 'count',        domain: 'text' },
  status:      { label: 'Status',       defaultWidth: 150, defaultAgg: 'distribution', domain: 'label' },
  priority:    { label: 'Priority',     defaultWidth: 130, defaultAgg: 'distribution', domain: 'label' },
  people:      { label: 'Owner',        defaultWidth: 150, defaultAgg: 'count',        domain: 'people' },
  date:        { label: 'Due date',     defaultWidth: 140, defaultAgg: 'none',         domain: 'date' },
  timeline:    { label: 'Timeline',     defaultWidth: 180, defaultAgg: 'none',         domain: 'date' },
  number:      { label: 'Number',       defaultWidth: 120, defaultAgg: 'sum',          domain: 'number' },
  text:        { label: 'Text',         defaultWidth: 180, defaultAgg: 'none',         domain: 'text' },
  tags:        { label: 'Tags',         defaultWidth: 160, defaultAgg: 'none',         domain: 'label' },
  dropdown:    { label: 'Dropdown',     defaultWidth: 150, defaultAgg: 'distribution', domain: 'label' },
  progress:    { label: 'Progress',     defaultWidth: 140, defaultAgg: 'avg',          domain: 'number' },
  rating:      { label: 'Rating',       defaultWidth: 120, defaultAgg: 'avg',          domain: 'number' },
  checkbox:    { label: 'Checkbox',     defaultWidth: 90,  defaultAgg: 'count',        domain: 'bool' },
  link:        { label: 'Link',         defaultWidth: 160, defaultAgg: 'none',         domain: 'text' },
  dependency:  { label: 'Dependencies', defaultWidth: 130, defaultAgg: 'none',         domain: 'meta' },
  upstream:    { label: 'Upstream Input',    defaultWidth: 200, defaultAgg: 'none',    domain: 'meta' },
  downstream:  { label: 'Downstream Target', defaultWidth: 200, defaultAgg: 'none',    domain: 'meta' },
  tokens:      { label: 'Tokens',       defaultWidth: 120, defaultAgg: 'sum',          domain: 'number' },
  cost:        { label: 'Cost',         defaultWidth: 110, defaultAgg: 'sum',          domain: 'number' },
  trigger:     { label: 'Trigger',      defaultWidth: 130, defaultAgg: 'distribution', domain: 'label' },
  createdAt:   { label: 'Created',      defaultWidth: 140, defaultAgg: 'none',         domain: 'date' },
  lastUpdated: { label: 'Last updated', defaultWidth: 140, defaultAgg: 'none',         domain: 'date' },
  itemId:      { label: 'Item ID',      defaultWidth: 120, defaultAgg: 'none',         domain: 'meta' },
  autoNumber:  { label: 'Auto №',       defaultWidth: 90,  defaultAgg: 'none',         domain: 'meta' },
  formula:     { label: 'Formula',      defaultWidth: 130, defaultAgg: 'sum',          domain: 'number' },
};

/** The columns shown on a fresh board, in order. */
export function defaultColumns(): ColumnDef[] {
  const mk = (id: string, kind: ColumnKind, title?: string): ColumnDef => ({
    id, kind, title: title ?? KIND_META[kind].label,
    width: KIND_META[kind].defaultWidth, aggregation: KIND_META[kind].defaultAgg,
  });
  return [
    mk('title', 'title'),
    mk('status', 'status'),
    mk('people', 'people'),
    mk('priority', 'priority'),
    mk('date', 'date'),
    mk('upstream', 'upstream'),
    mk('downstream', 'downstream'),
    mk('tokens', 'tokens'),
    mk('createdAt', 'createdAt'),
  ];
}

// ── Value accessors ──────────────────────────────────────────────────────────

export function personFor(task: AgentTask, ctx: CellContext): Person | undefined {
  return ctx.peopleById[task.assignee.id];
}

function runFor(task: AgentTask, ctx: CellContext): WorkflowRun | undefined {
  return task.workflowRunId ? ctx.runsById[task.workflowRunId] : undefined;
}

/** Numeric value of a cell, or null. Used for sorting and numeric aggregation. */
export function cellNumber(task: AgentTask, col: ColumnDef, ctx: CellContext): number | null {
  switch (col.kind) {
    case 'tokens': return runFor(task, ctx)?.totalTokens ?? null;
    case 'cost': return runFor(task, ctx)?.estimatedCostUsd ?? null;
    case 'progress':
    case 'rating':
    case 'number': {
      const v = ctx.customCells[task.id]?.[col.id];
      return v != null && v !== '' ? Number(v) : null;
    }
    case 'formula': return evalFormula(col, task, ctx);
    case 'date': return task.dueAt ? new Date(task.dueAt).getTime() : null;
    case 'createdAt': return new Date(task.createdAt).getTime();
    default: return null;
  }
}

/** A comparable key for sorting/grouping. */
export function cellSortKey(task: AgentTask, col: ColumnDef, ctx: CellContext): string | number {
  const n = cellNumber(task, col, ctx);
  if (n != null) return n;
  return cellText(task, col, ctx).toLowerCase();
}

/** The group bucket key + its label + color, for grouping rows. */
export function cellGroup(task: AgentTask, col: ColumnDef, ctx: CellContext): { key: string; label: string; hex: string } {
  switch (col.kind) {
    case 'status': return { key: task.status, label: STATUS_CONFIG[task.status].label, hex: STATUS_CONFIG[task.status].hex };
    case 'priority': return { key: task.priority, label: PRIORITY_CONFIG[task.priority].label, hex: PRIORITY_CONFIG[task.priority].hex };
    case 'people': {
      const p = personFor(task, ctx);
      const name = p?.name ?? displayName(task.assignee.id);
      return { key: task.assignee.id, label: name, hex: p?.hex ?? colorFor(task.assignee.id) };
    }
    default: {
      const txt = cellText(task, col, ctx) || '—';
      return { key: txt, label: txt, hex: colorFor(txt) };
    }
  }
}

/** Plain-text representation for display fallbacks and search. */
export function cellText(task: AgentTask, col: ColumnDef, ctx: CellContext): string {
  switch (col.kind) {
    case 'title': return task.title;
    case 'text': return ctx.customCells[task.id]?.[col.id] ?? '';
    case 'status': return STATUS_CONFIG[task.status].label;
    case 'priority': return PRIORITY_CONFIG[task.priority].label;
    case 'people': return personFor(task, ctx)?.name ?? displayName(task.assignee.id);
    case 'date': return task.dueAt ? formatDate(task.dueAt) : '';
    case 'createdAt': return formatDate(task.createdAt);
    case 'lastUpdated': return formatDate(task.createdAt);
    case 'timeline': return task.dueAt ? `${formatDate(task.createdAt)} – ${formatDate(task.dueAt)}` : '';
    case 'dependency': return `${(ctx.upstreamIds[task.id]?.length ?? 0)}↑ ${(ctx.downstreamIds[task.id]?.length ?? 0)}↓`;
    case 'upstream': return (ctx.upstreamIds[task.id] ?? []).map(id => ctx.tasksById[id]?.title ?? id).join(', ');
    case 'downstream': return (ctx.downstreamIds[task.id] ?? []).map(id => ctx.tasksById[id]?.title ?? id).join(', ');
    case 'tokens': return String(runFor(task, ctx)?.totalTokens ?? '');
    case 'cost': { const c = runFor(task, ctx)?.estimatedCostUsd; return c != null ? `$${c.toFixed(2)}` : ''; }
    case 'trigger': return task.triggerSource ?? 'Manual';
    case 'itemId': return task.id;
    case 'tags':
    case 'dropdown':
    case 'link':
    case 'number':
    case 'progress':
    case 'rating': return ctx.customCells[task.id]?.[col.id] ?? '';
    case 'checkbox': return ctx.customCells[task.id]?.[col.id] === 'true' ? '✓' : '';
    case 'formula': { const n = evalFormula(col, task, ctx); return n == null ? '' : String(n); }
    default: return '';
  }
}

/** Whether a cell is considered empty (for isEmpty filters / countEmpty agg). */
export function cellEmpty(task: AgentTask, col: ColumnDef, ctx: CellContext): boolean {
  if (col.kind === 'date') return !task.dueAt;
  if (col.kind === 'people') return !task.assignee.id;
  return cellText(task, col, ctx).trim() === '';
}

// ── Formula evaluation (safe, tiny) ──────────────────────────────────────────
// Supports {colId} references and + - * / ( ) and numbers. No JS eval.

export function evalFormula(col: ColumnDef, task: AgentTask, ctx: CellContext): number | null {
  if (!col.formula) return null;
  const substituted = col.formula.replace(/\{([a-zA-Z0-9_-]+)\}/g, (_, ref) => {
    const refCol = { id: ref, kind: guessKind(ref), title: ref, width: 100 } as ColumnDef;
    const n = cellNumber(task, refCol, ctx);
    return n == null ? '0' : String(n);
  });
  return safeArithmetic(substituted);
}

function guessKind(ref: string): ColumnKind {
  if (ref === 'tokens') return 'tokens';
  if (ref === 'cost') return 'cost';
  return 'number';
}

/** Evaluate a basic arithmetic expression without eval(). */
export function safeArithmetic(expr: string): number | null {
  const tokens = expr.match(/\d+\.?\d*|[+\-*/()]/g);
  if (!tokens) return null;
  let pos = 0;
  const peek = () => tokens[pos];
  const next = () => tokens[pos++];
  function parseExpr(): number { let v = parseTerm(); while (peek() === '+' || peek() === '-') { const op = next(); const r = parseTerm(); v = op === '+' ? v + r : v - r; } return v; }
  function parseTerm(): number { let v = parseFactor(); while (peek() === '*' || peek() === '/') { const op = next(); const r = parseFactor(); v = op === '*' ? v * r : (r === 0 ? 0 : v / r); } return v; }
  function parseFactor(): number { const t = next(); if (t === '(') { const v = parseExpr(); if (peek() === ')') next(); return v; } return parseFloat(t); }
  try { const result = parseExpr(); return isFinite(result) ? Math.round(result * 1000) / 1000 : null; }
  catch { return null; }
}

// ── Aggregation ──────────────────────────────────────────────────────────────

export interface AggregationResult {
  kind: ColumnAggregation;
  label: string;       // e.g. "Sum"
  value: string;       // formatted scalar value (when not distribution)
  distribution?: { label: string; hex: string; count: number }[];
}

export function aggregate(tasks: AgentTask[], col: ColumnDef, ctx: CellContext): AggregationResult | null {
  const agg = col.aggregation ?? KIND_META[col.kind].defaultAgg;
  if (agg === 'none') return null;

  if (agg === 'distribution') {
    const buckets = new Map<string, { label: string; hex: string; count: number }>();
    for (const t of tasks) {
      const g = cellGroup(t, col, ctx);
      const cur = buckets.get(g.key);
      if (cur) cur.count++;
      else buckets.set(g.key, { label: g.label, hex: g.hex, count: 1 });
    }
    return { kind: agg, label: 'Distribution', value: '', distribution: [...buckets.values()].sort((a, b) => b.count - a.count) };
  }

  if (agg === 'count') return { kind: agg, label: 'Count', value: String(tasks.length) };
  if (agg === 'countEmpty') return { kind: agg, label: 'Empty', value: String(tasks.filter(t => cellEmpty(t, col, ctx)).length) };

  const nums = tasks.map(t => cellNumber(t, col, ctx)).filter((n): n is number => n != null);
  if (nums.length === 0) return { kind: agg, label: aggLabel(agg), value: '—' };
  let v: number;
  switch (agg) {
    case 'sum': v = nums.reduce((a, b) => a + b, 0); break;
    case 'avg': v = nums.reduce((a, b) => a + b, 0) / nums.length; break;
    case 'min': v = Math.min(...nums); break;
    case 'max': v = Math.max(...nums); break;
    case 'median': { const s = [...nums].sort((a, b) => a - b); const m = Math.floor(s.length / 2); v = s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; break; }
    default: v = 0;
  }
  const fmt = col.kind === 'cost' ? `$${v.toFixed(2)}` : Number.isInteger(v) ? String(v) : v.toFixed(1);
  return { kind: agg, label: aggLabel(agg), value: fmt };
}

function aggLabel(a: ColumnAggregation): string {
  return ({ sum: 'Sum', avg: 'Average', min: 'Min', max: 'Max', median: 'Median', count: 'Count', countEmpty: 'Empty', distribution: 'Distribution', none: '' } as Record<ColumnAggregation, string>)[a];
}
