'use client';

import React, { createContext, useContext, useEffect, useMemo, useRef, useState, useCallback } from 'react';
import { api, type TaskPatch } from './api';
import type {
  Board, AgentTask, AgentRole, WorkflowRun, Person,
  ColumnDef, ColumnKind, ViewDef, ViewKind, FilterGroup, SortRule,
  Comment, ActivityEntry, AutomationRecipe, AgentTaskStatus,
} from './types';
import { defaultColumns, KIND_META, type CellContext } from './columns';
import { applyFilter, applySearch, applySort, groupTasks, type TaskGroup } from './board-engine';
import { buildPeople, seedCollaboration, uid, CURRENT_USER } from './collaboration';
import { STATUS_CONFIG, PRIORITY_CONFIG } from './palette';
import { toast } from './toast';
import { runAutomations } from './automations';

// ── Persisted per-board config ────────────────────────────────────────────────

interface BoardConfig {
  views: ViewDef[];
  activeViewId: string;
  columns: ColumnDef[];
  customCells: Record<string, Record<string, string>>;
  comments: Comment[];
  activity: ActivityEntry[];
  automations: AutomationRecipe[];
  collapsedGroups: string[];
  /** Flow-canvas edge labels, keyed by `${source}->${target}`. */
  edgeLabels: Record<string, string>;
}

function defaultViews(): ViewDef[] {
  return [
    { id: 'v-table', name: 'Main Table', kind: 'table', groupBy: 'status', sorts: [], builtIn: true },
    { id: 'v-kanban', name: 'Kanban', kind: 'kanban', groupBy: 'status', builtIn: true },
    { id: 'v-timeline', name: 'Timeline', kind: 'timeline', dateColumnId: 'date', builtIn: true },
    { id: 'v-calendar', name: 'Calendar', kind: 'calendar', dateColumnId: 'date', builtIn: true },
    { id: 'v-cards', name: 'Cards', kind: 'cards', builtIn: true },
    { id: 'v-chart', name: 'Chart', kind: 'chart', chart: { type: 'bar', groupBy: 'status', metric: 'count' }, builtIn: true },
    { id: 'v-canvas', name: 'Flow Canvas', kind: 'canvas', builtIn: true },
  ];
}

function defaultConfig(): BoardConfig {
  return {
    views: defaultViews(),
    activeViewId: 'v-table',
    columns: defaultColumns(),
    customCells: {},
    comments: [],
    activity: [],
    automations: [],
    collapsedGroups: [],
    edgeLabels: {},
  };
}

function storageKey(boardId: string) { return `tectika:board:${boardId}`; }

function loadConfig(boardId: string): BoardConfig | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = localStorage.getItem(storageKey(boardId));
    if (!raw) return null;
    return { ...defaultConfig(), ...JSON.parse(raw) };
  } catch { return null; }
}

// ── Context shape ───────────────────────────────────────────────────────────

interface BoardContextValue {
  loading: boolean;
  error?: string;
  board?: Board;
  tasks: AgentTask[];
  roles: AgentRole[];
  runsById: Record<string, WorkflowRun>;
  peopleById: Record<string, Person>;
  people: Person[];
  cellContext: CellContext;

  // view + columns
  views: ViewDef[];
  activeView: ViewDef;
  columns: ColumnDef[];
  visibleColumns: ColumnDef[];
  setActiveView: (id: string) => void;
  createView: (name: string, kind: ViewKind) => void;
  updateActiveView: (patch: Partial<ViewDef>) => void;
  renameView: (id: string, name: string) => void;
  deleteView: (id: string) => void;
  setColumns: (cols: ColumnDef[]) => void;
  toggleColumnHidden: (id: string) => void;
  resizeColumn: (id: string, width: number) => void;
  addColumn: (kind: ColumnKind, title?: string) => void;
  removeColumn: (id: string) => void;
  setAggregation: (id: string, agg: ColumnDef['aggregation']) => void;

  // search / filter / sort / group
  search: string;
  setSearch: (q: string) => void;
  setFilter: (f: FilterGroup | undefined) => void;
  setSorts: (s: SortRule[]) => void;
  setGroupBy: (colId: string | undefined) => void;

  // derived data
  groups: TaskGroup[];
  visibleTasks: AgentTask[];

  // groups ui
  collapsedGroups: string[];
  toggleGroup: (key: string) => void;

  // selection
  selectedIds: string[];
  toggleSelect: (id: string, range?: boolean) => void;
  selectAll: (ids: string[]) => void;
  clearSelection: () => void;

  // mutations
  updateTask: (id: string, patch: TaskPatch, opts?: { silent?: boolean }) => Promise<void>;
  setStatus: (id: string, status: AgentTaskStatus) => Promise<void>;
  setCustomCell: (taskId: string, colId: string, value: string) => void;
  addTask: (partial: Partial<AgentTask> & { title: string }, groupValue?: { colId: string; key: string }) => Promise<AgentTask | null>;
  deleteTasks: (ids: string[]) => Promise<void>;
  connectTasks: (upstreamId: string, downstreamId: string) => Promise<void>;
  disconnectTasks: (upstreamId: string, downstreamId: string) => Promise<void>;
  moveCanvas: (id: string, x: number, y: number) => void;
  edgeLabels: Record<string, string>;
  setEdgeLabel: (source: string, target: string, label: string) => void;

  // item panel
  openTaskId?: string;
  openTask: (id: string | undefined) => void;

  // collaboration
  comments: Comment[];
  activity: ActivityEntry[];
  addComment: (taskId: string, body: string) => void;
  logActivity: (e: Omit<ActivityEntry, 'id' | 'createdAt'>) => void;

  // automations
  automations: AutomationRecipe[];
  saveAutomation: (a: AutomationRecipe) => void;
  deleteAutomation: (id: string) => void;
  toggleAutomation: (id: string) => void;
}

const Ctx = createContext<BoardContextValue | null>(null);

export function useBoard(): BoardContextValue {
  const v = useContext(Ctx);
  if (!v) throw new Error('useBoard must be used within BoardProvider');
  return v;
}

// ── Provider ──────────────────────────────────────────────────────────────────

export function BoardProvider({ boardId, children }: { boardId: string; children: React.ReactNode }) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [board, setBoard] = useState<Board>();
  const [tasks, setTasks] = useState<AgentTask[]>([]);
  const [roles, setRoles] = useState<AgentRole[]>([]);
  const [runsById, setRunsById] = useState<Record<string, WorkflowRun>>({});

  const [cfg, setCfg] = useState<BoardConfig>(defaultConfig);
  const [search, setSearch] = useState('');
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [openTaskId, setOpenTaskId] = useState<string>();
  const lastSelected = useRef<string | null>(null);
  const hydrated = useRef(false);

  // ── load ────────────────────────────────────────────────────────────────────
  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional: load board data on mount/boardId change
    setLoading(true);
    (async () => {
      try {
        const [b, t, r] = await Promise.all([
          api.boards.get(boardId).catch(() => undefined),
          api.tasks.list(boardId),
          api.agentRoles.list().catch(() => []),
        ]);
        if (cancelled) return;
        setBoard(b);
        setTasks(t);
        setRoles(r);

        // fetch runs for tasks that have one
        const runTasks = t.filter(x => x.workflowRunId);
        const runs = await Promise.all(
          runTasks.map(x => api.runs.get(x.id, x.workflowRunId!).catch(() => null)),
        );
        if (cancelled) return;
        const map: Record<string, WorkflowRun> = {};
        runs.forEach(run => { if (run) map[run.id] = run; });
        setRunsById(map);

        // hydrate persisted config (or seed defaults + collaboration)
        const saved = loadConfig(boardId);
        if (saved) {
          setCfg(saved);
        } else {
          const seed = seedCollaboration(t);
          setCfg({ ...defaultConfig(), comments: seed.comments, activity: seed.activity });
        }
        hydrated.current = true;
        setLoading(false);
      } catch (e) {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : 'Failed to load board');
        setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [boardId]);

  // ── persist config ────────────────────────────────────────────────────────────
  useEffect(() => {
    if (!hydrated.current || typeof window === 'undefined') return;
    try { localStorage.setItem(storageKey(boardId), JSON.stringify(cfg)); } catch { /* quota */ }
  }, [cfg, boardId]);

  // ── derived ─────────────────────────────────────────────────────────────────
  const peopleById = useMemo(() => buildPeople(roles, tasks), [roles, tasks]);
  const people = useMemo(() => Object.values(peopleById), [peopleById]);
  const tasksById = useMemo(() => Object.fromEntries(tasks.map(t => [t.id, t])), [tasks]);

  const cellContext: CellContext = useMemo(
    () => ({ roles, peopleById, runsById, customCells: cfg.customCells, tasksById }),
    [roles, peopleById, runsById, cfg.customCells, tasksById],
  );

  const activeView = useMemo(
    () => cfg.views.find(v => v.id === cfg.activeViewId) ?? cfg.views[0],
    [cfg.views, cfg.activeViewId],
  );

  const visibleColumns = useMemo(() => cfg.columns.filter(c => !c.hidden), [cfg.columns]);

  const visibleTasks = useMemo(() => {
    let t = applyFilter(tasks, activeView?.filter, cfg.columns, cellContext);
    t = applySearch(t, search, cfg.columns, cellContext);
    t = applySort(t, activeView?.sorts, cfg.columns, cellContext);
    return t;
  }, [tasks, activeView, cfg.columns, cellContext, search]);

  const groups = useMemo(
    () => groupTasks(visibleTasks, activeView?.groupBy, cfg.columns, cellContext),
    [visibleTasks, activeView, cfg.columns, cellContext],
  );

  // ── config mutators ─────────────────────────────────────────────────────────
  const patchCfg = useCallback((p: Partial<BoardConfig>) => setCfg(prev => ({ ...prev, ...p })), []);
  const patchView = useCallback((id: string, patch: Partial<ViewDef>) => {
    setCfg(prev => ({ ...prev, views: prev.views.map(v => v.id === id ? { ...v, ...patch } : v) }));
  }, []);

  const setActiveView = useCallback((id: string) => { setCfg(prev => ({ ...prev, activeViewId: id })); setSelectedIds([]); }, []);
  const updateActiveView = useCallback((patch: Partial<ViewDef>) => patchView(cfg.activeViewId, patch), [cfg.activeViewId, patchView]);

  const createView = useCallback((name: string, kind: ViewKind) => {
    const id = uid('v');
    const v: ViewDef = { id, name, kind, groupBy: kind === 'table' || kind === 'kanban' ? 'status' : undefined, dateColumnId: kind === 'timeline' || kind === 'calendar' ? 'date' : undefined, chart: kind === 'chart' ? { type: 'bar', groupBy: 'status', metric: 'count' } : undefined };
    setCfg(prev => ({ ...prev, views: [...prev.views, v], activeViewId: id }));
  }, []);
  const renameView = useCallback((id: string, name: string) => patchView(id, { name }), [patchView]);
  const deleteView = useCallback((id: string) => {
    setCfg(prev => {
      const views = prev.views.filter(v => v.id !== id);
      return { ...prev, views, activeViewId: prev.activeViewId === id ? views[0]?.id : prev.activeViewId };
    });
  }, []);

  const setColumns = useCallback((cols: ColumnDef[]) => patchCfg({ columns: cols }), [patchCfg]);
  const toggleColumnHidden = useCallback((id: string) => setCfg(prev => ({ ...prev, columns: prev.columns.map(c => c.id === id ? { ...c, hidden: !c.hidden } : c) })), []);
  const resizeColumn = useCallback((id: string, width: number) => setCfg(prev => ({ ...prev, columns: prev.columns.map(c => c.id === id ? { ...c, width: Math.max(60, width) } : c) })), []);
  const addColumn = useCallback((kind: ColumnKind, title?: string) => {
    const id = uid('col');
    const meta = KIND_META[kind];
    const col: ColumnDef = { id, kind, title: title ?? meta.label, width: meta.defaultWidth, aggregation: meta.defaultAgg, custom: true };
    setCfg(prev => ({ ...prev, columns: [...prev.columns, col] }));
  }, []);
  const removeColumn = useCallback((id: string) => setCfg(prev => ({ ...prev, columns: prev.columns.filter(c => c.id !== id) })), []);
  const setAggregation = useCallback((id: string, agg: ColumnDef['aggregation']) => setCfg(prev => ({ ...prev, columns: prev.columns.map(c => c.id === id ? { ...c, aggregation: agg } : c) })), []);

  const setFilter = useCallback((f: FilterGroup | undefined) => updateActiveView({ filter: f }), [updateActiveView]);
  const setSorts = useCallback((s: SortRule[]) => updateActiveView({ sorts: s }), [updateActiveView]);
  const setGroupBy = useCallback((colId: string | undefined) => updateActiveView({ groupBy: colId }), [updateActiveView]);

  const toggleGroup = useCallback((key: string) => setCfg(prev => ({
    ...prev,
    collapsedGroups: prev.collapsedGroups.includes(key) ? prev.collapsedGroups.filter(k => k !== key) : [...prev.collapsedGroups, key],
  })), []);

  // ── selection ─────────────────────────────────────────────────────────────────
  const clearSelection = useCallback(() => setSelectedIds([]), []);
  const selectAll = useCallback((ids: string[]) => setSelectedIds(ids), []);
  const toggleSelect = useCallback((id: string, range?: boolean) => {
    setSelectedIds(prev => {
      if (range && lastSelected.current) {
        const order = visibleTasks.map(t => t.id);
        const a = order.indexOf(lastSelected.current);
        const b = order.indexOf(id);
        if (a >= 0 && b >= 0) {
          const [lo, hi] = a < b ? [a, b] : [b, a];
          const rangeIds = order.slice(lo, hi + 1);
          return Array.from(new Set([...prev, ...rangeIds]));
        }
      }
      lastSelected.current = id;
      return prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id];
    });
  }, [visibleTasks]);

  // ── collaboration ───────────────────────────────────────────────────────────
  const logActivity = useCallback((e: Omit<ActivityEntry, 'id' | 'createdAt'>) => {
    setCfg(prev => ({ ...prev, activity: [...prev.activity, { ...e, id: uid('a'), createdAt: new Date().toISOString() }] }));
  }, []);
  const addComment = useCallback((taskId: string, body: string) => {
    const mentions = Array.from(body.matchAll(/@([\w.@-]+)/g)).map(m => m[1]);
    const c: Comment = { id: uid('c'), taskId, authorId: CURRENT_USER.id, body, mentions, createdAt: new Date().toISOString() };
    setCfg(prev => ({ ...prev, comments: [...prev.comments, c] }));
  }, []);

  // ── automations ─────────────────────────────────────────────────────────────
  const saveAutomation = useCallback((a: AutomationRecipe) => setCfg(prev => ({
    ...prev,
    automations: prev.automations.some(x => x.id === a.id)
      ? prev.automations.map(x => x.id === a.id ? a : x)
      : [...prev.automations, a],
  })), []);
  const deleteAutomation = useCallback((id: string) => setCfg(prev => ({ ...prev, automations: prev.automations.filter(a => a.id !== id) })), []);
  const toggleAutomation = useCallback((id: string) => setCfg(prev => ({ ...prev, automations: prev.automations.map(a => a.id === id ? { ...a, enabled: !a.enabled } : a) })), []);

  // ── task mutations ────────────────────────────────────────────────────────────
  const updateTask = useCallback(async (id: string, patch: TaskPatch, opts?: { silent?: boolean }) => {
    let before: AgentTask | undefined;
    setTasks(prev => prev.map(t => { if (t.id === id) { before = t; return { ...t, ...patch }; } return t; }));
    try {
      const updated = await api.tasks.update(boardId, id, patch);
      setTasks(prev => prev.map(t => t.id === id ? updated : t));
      // activity + automations on status change
      if (patch.status && before && before.status !== patch.status) {
        logActivity({ taskId: id, kind: 'status', actorId: CURRENT_USER.id, from: STATUS_CONFIG[before.status].label, to: STATUS_CONFIG[patch.status].label });
      }
      if (patch.priority && before && before.priority !== patch.priority) {
        logActivity({ taskId: id, kind: 'priority', actorId: CURRENT_USER.id, from: PRIORITY_CONFIG[before.priority].label, to: PRIORITY_CONFIG[patch.priority].label });
      }
    } catch {
      if (before) { const b = before; setTasks(prev => prev.map(t => t.id === id ? b : t)); }
      if (!opts?.silent) toast('Could not save change', 'error');
    }
  }, [boardId, logActivity]);

  const setStatus = useCallback(async (id: string, status: AgentTaskStatus) => {
    await updateTask(id, { status });
    // run client-side automations
    const task = tasksById[id];
    if (task) {
      runAutomations(cfg.automations, { type: 'statusBecomes', task: { ...task, status } }, {
        setStatus: (tid, s) => updateTask(tid, { status: s }, { silent: true }),
        notify: (msg) => toast(msg, 'info'),
      });
    }
  }, [updateTask, tasksById, cfg.automations]);

  const setCustomCell = useCallback((taskId: string, colId: string, value: string) => {
    setCfg(prev => ({
      ...prev,
      customCells: { ...prev.customCells, [taskId]: { ...prev.customCells[taskId], [colId]: value } },
    }));
  }, []);

  const addTask = useCallback(async (partial: Partial<AgentTask> & { title: string }, groupValue?: { colId: string; key: string }) => {
    // apply group value (e.g. created inside a Status group inherits that status)
    const seed: Partial<AgentTask> = { priority: 'Medium', assignee: { type: 'Human', id: CURRENT_USER.id }, ...partial };
    if (groupValue) {
      if (groupValue.colId === 'status') seed.status = groupValue.key as AgentTaskStatus;
      if (groupValue.colId === 'priority') seed.priority = groupValue.key as AgentTask['priority'];
      if (groupValue.colId === 'people') seed.assignee = { type: 'Human', id: groupValue.key };
    }
    try {
      const created = await api.tasks.create(boardId, seed);
      setTasks(prev => [...prev, created]);
      logActivity({ taskId: created.id, kind: 'created', actorId: CURRENT_USER.id });
      return created;
    } catch {
      toast('Could not create item', 'error');
      return null;
    }
  }, [boardId, logActivity]);

  const deleteTasks = useCallback(async (ids: string[]) => {
    const set = new Set(ids);
    const removed = tasks.filter(t => set.has(t.id));
    setTasks(prev => prev.filter(t => !set.has(t.id)));
    setSelectedIds([]);
    try {
      await Promise.all(ids.map(id => api.tasks.remove(boardId, id)));
      toast(`${ids.length} item${ids.length > 1 ? 's' : ''} deleted`, 'success');
    } catch {
      setTasks(prev => [...prev, ...removed]);
      toast('Could not delete items', 'error');
    }
  }, [boardId, tasks]);

  const connectTasks = useCallback(async (upstreamId: string, downstreamId: string) => {
    if (upstreamId === downstreamId) return;
    setTasks(prev => prev.map(t => {
      if (t.id === upstreamId && !t.downstreamTaskIds.includes(downstreamId)) return { ...t, downstreamTaskIds: [...t.downstreamTaskIds, downstreamId] };
      if (t.id === downstreamId && !t.upstreamTaskIds.includes(upstreamId)) return { ...t, upstreamTaskIds: [...t.upstreamTaskIds, upstreamId] };
      return t;
    }));
    try { await api.tasks.connect(boardId, upstreamId, downstreamId); logActivity({ taskId: downstreamId, kind: 'connected', actorId: CURRENT_USER.id, from: upstreamId }); }
    catch { toast('Could not connect items', 'error'); }
  }, [boardId, logActivity]);

  const disconnectTasks = useCallback(async (upstreamId: string, downstreamId: string) => {
    setTasks(prev => prev.map(t => {
      if (t.id === upstreamId) return { ...t, downstreamTaskIds: t.downstreamTaskIds.filter(d => d !== downstreamId) };
      if (t.id === downstreamId) return { ...t, upstreamTaskIds: t.upstreamTaskIds.filter(u => u !== upstreamId) };
      return t;
    }));
    const up = tasksById[upstreamId];
    const down = tasksById[downstreamId];
    try {
      if (up) await api.tasks.update(boardId, upstreamId, { downstreamTaskIds: up.downstreamTaskIds.filter(d => d !== downstreamId) });
      if (down) await api.tasks.update(boardId, downstreamId, { upstreamTaskIds: down.upstreamTaskIds.filter(u => u !== upstreamId) });
    } catch { toast('Could not update connection', 'error'); }
  }, [boardId, tasksById]);

  const moveCanvas = useCallback((id: string, x: number, y: number) => {
    setTasks(prev => prev.map(t => t.id === id ? { ...t, canvasPosition: { x, y } } : t));
    api.tasks.updatePosition(boardId, id, x, y).catch(() => {});
  }, [boardId]);

  const setEdgeLabel = useCallback((source: string, target: string, label: string) => {
    const key = `${source}->${target}`;
    setCfg(prev => {
      const next = { ...prev.edgeLabels };
      if (label.trim()) next[key] = label.trim(); else delete next[key];
      return { ...prev, edgeLabels: next };
    });
  }, []);

  const value: BoardContextValue = {
    loading, error, board, tasks, roles, runsById, peopleById, people, cellContext,
    views: cfg.views, activeView, columns: cfg.columns, visibleColumns,
    setActiveView, createView, updateActiveView, renameView, deleteView,
    setColumns, toggleColumnHidden, resizeColumn, addColumn, removeColumn, setAggregation,
    search, setSearch, setFilter, setSorts, setGroupBy,
    groups, visibleTasks,
    collapsedGroups: cfg.collapsedGroups, toggleGroup,
    selectedIds, toggleSelect, selectAll, clearSelection,
    updateTask, setStatus, setCustomCell, addTask, deleteTasks, connectTasks, disconnectTasks, moveCanvas,
    edgeLabels: cfg.edgeLabels, setEdgeLabel,
    openTaskId, openTask: setOpenTaskId,
    comments: cfg.comments, activity: cfg.activity, addComment, logActivity,
    automations: cfg.automations, saveAutomation, deleteAutomation, toggleAutomation,
  };

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
