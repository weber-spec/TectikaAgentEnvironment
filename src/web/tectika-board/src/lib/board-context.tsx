'use client';

import React, { createContext, useContext, useEffect, useMemo, useRef, useState, useCallback } from 'react';
import { api, type TaskPatch } from './api';
import type {
  Board, AgentTask, AgentRole, WorkflowRun, Person, TaskEdge,
  ColumnDef, ColumnKind, ViewDef, ViewKind, FilterGroup, SortRule,
  Comment, ActivityEntry, AutomationRecipe, AgentTaskStatus, ChatTurn, BoardRunPhase, RunStatus,
  UsageRollup,
} from './types';
import { defaultColumns, KIND_META, type CellContext } from './columns';
import { applyFilter, applySearch, applySort, groupTasks, type TaskGroup } from './board-engine';
import { buildPeople, seedCollaboration, uid, CURRENT_USER } from './collaboration';
import { STATUS_CONFIG, PRIORITY_CONFIG } from './palette';
import { toast } from './toast';
import { runAutomations } from './automations';

/**
 * Run statuses where the run is no longer actively executing — finished
 * (Completed/Failed/Cancelled) or paused awaiting a human (PausedApproval /
 * AwaitingInteraction / NeedsRevision). Used to stop the live run poll and to
 * detect Run Board batch completion.
 */
export const TERMINAL_RUN_STATUSES: ReadonlySet<RunStatus> = new Set<RunStatus>([
  'PausedApproval', 'AwaitingInteraction', 'NeedsRevision', 'Completed', 'Failed', 'Cancelled',
]);

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
  /** Interactive agent-workspace conversations, keyed by taskId. */
  chatThreads: Record<string, ChatTurn[]>;
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
    chatThreads: {},
  };
}

function storageKey(boardId: string) { return `tectika:board:${boardId}`; }

function loadConfig(boardId: string): BoardConfig | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = localStorage.getItem(storageKey(boardId));
    if (!raw) return null;
    const cfg = { ...defaultConfig(), ...JSON.parse(raw) } as BoardConfig;
    cfg.columns = migrateColumns(cfg.columns);
    return cfg;
  } catch { return null; }
}

/** Upgrade older saved layouts: replace the built-in counts "Dependencies" column
 *  with the explicit Upstream Input / Downstream Target columns. */
function migrateColumns(columns: ColumnDef[]): ColumnDef[] {
  if (columns.some(c => c.kind === 'upstream' || c.kind === 'downstream')) return columns;
  const i = columns.findIndex(c => c.id === 'dependency' && c.kind === 'dependency');
  if (i === -1) return columns;
  const mk = (id: string, kind: ColumnKind): ColumnDef =>
    ({ id, kind, title: KIND_META[kind].label, width: KIND_META[kind].defaultWidth, aggregation: KIND_META[kind].defaultAgg });
  return [...columns.slice(0, i), mk('upstream', 'upstream'), mk('downstream', 'downstream'), ...columns.slice(i + 1)];
}

// ── Context shape ───────────────────────────────────────────────────────────

interface BoardContextValue {
  loading: boolean;
  error?: string;
  board?: Board;
  tasks: AgentTask[];
  roles: AgentRole[];
  runsById: Record<string, WorkflowRun>;
  usageByTaskId: Record<string, UsageRollup>;
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
  refreshTask: (id: string) => Promise<void>;
  refreshUsage: (id: string) => Promise<void>;
  setStatus: (id: string, status: AgentTaskStatus) => Promise<void>;
  setCustomCell: (taskId: string, colId: string, value: string) => void;
  addTask: (partial: Partial<AgentTask> & { title: string }, groupValue?: { colId: string; key: string }) => Promise<AgentTask | null>;
  deleteTasks: (ids: string[]) => Promise<void>;
  moveCanvas: (id: string, x: number, y: number) => void;

  // typed edges (server-persisted source of truth for the task graph)
  edges: TaskEdge[];
  /** Dependency-edge upstream ids per taskId (sources feeding INTO the task). */
  upstreamIds: Record<string, string[]>;
  /** Dependency-edge downstream ids per taskId (targets the task routes output TO). */
  downstreamIds: Record<string, string[]>;
  connectEdge: (source: string, target: string) => Promise<TaskEdge | undefined>;
  disconnectEdge: (edgeId: string) => Promise<void>;
  updateEdge: (edgeId: string, patch: Partial<Pick<TaskEdge, 'kind' | 'label' | 'maxIterations'>>) => Promise<void>;

  // run board
  runBoard: () => Promise<void>;
  /** Cancel an in-progress board run (stops every task in the batch). */
  stopBoard: () => Promise<void>;
  runPhase: BoardRunPhase;
  /** How many tasks a board run would launch right now (0 ⇒ nothing to run). */
  boardRunnableCount: number;
  /** Of {@link boardRunnableCount}, how many are Failed tasks the run would reset and retry. */
  boardRetryCount: number;
  /** Trigger the agent for a single task. No-op unless the task is Agent-owned and idle. */
  runTask: (taskId: string) => Promise<void>;
  /** Reset a finished/non-Backlog task to Backlog and start a fresh run. */
  resetAndRun: (taskId: string) => Promise<void>;
  /** Cancel a task's active run (terminates the orchestration, returns the task to Backlog). */
  stopTask: (taskId: string) => Promise<void>;
  /** True while the given task's agent is actively running. */
  isTaskRunning: (task: AgentTask) => boolean;

  // agent config + interactive workspace chat
  saveRole: (role: AgentRole) => Promise<void>;
  chatThreads: Record<string, ChatTurn[]>;
  pushChatTurns: (taskId: string, turns: ChatTurn[]) => void;

  // item panel
  openTaskId?: string;
  openTask: (id: string | undefined) => void;
  repoChangesTarget?: string;            // head branch to open in the Repo "Changes" tab
  openRepoChanges: (head: string) => void;
  clearRepoChangesTarget: () => void;

  // live sync
  liveEnabled: boolean;
  liveState: 'live' | 'paused' | 'reconnecting';
  toggleLive: () => void;

  // collaboration
  activity: ActivityEntry[];
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
  const [edges, setEdges] = useState<TaskEdge[]>([]);
  const [roles, setRoles] = useState<AgentRole[]>([]);
  const [runsById, setRunsById] = useState<Record<string, WorkflowRun>>({});
  const [usageByTaskId, setUsageByTaskId] = useState<Record<string, UsageRollup>>({});

  const [cfg, setCfg] = useState<BoardConfig>(defaultConfig);
  const [search, setSearch] = useState('');
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [openTaskId, setOpenTaskId] = useState<string>();
  const [repoChangesTarget, setRepoChangesTarget] = useState<string | undefined>();
  const lastSelected = useRef<string | null>(null);
  const hydrated = useRef(false);

  // live sync (PRD §2 — SSE live updates, with a polling fallback)
  const [liveEnabled, setLiveEnabled] = useState(true);
  const [liveState, setLiveState] = useState<'live' | 'paused' | 'reconnecting'>('live');
  const lastEditRef = useRef(0);

  // run phase — tracks spinner/status-dot state for the Run Board button
  const [runPhase, setRunPhase] = useState<BoardRunPhase>(() => {
    if (typeof window === 'undefined') return { kind: 'idle' };
    try {
      const saved = localStorage.getItem(`tectika:board:${boardId}:runPhase`);
      return saved ? (JSON.parse(saved) as BoardRunPhase) : { kind: 'idle' };
    } catch { return { kind: 'idle' }; }
  });

  // ── load ────────────────────────────────────────────────────────────────────
  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional: load board data on mount/boardId change
    setLoading(true);
    (async () => {
      try {
        const [b, t, e, r] = await Promise.all([
          api.boards.get(boardId).catch(() => undefined),
          api.tasks.list(boardId),
          api.edges.list(boardId).catch(() => [] as TaskEdge[]),
          api.agentRoles.list().catch(() => []),
        ]);
        if (cancelled) return;
        setBoard(b);
        setTasks(t);
        setEdges(e);
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

        // fetch usage rollups for all tasks (resilient: a failure for one task is skipped)
        const usageResults = await Promise.all(
          t.map(x => api.usage.task(x.id).then(u => ({ id: x.id, u })).catch(() => null)),
        );
        if (cancelled) return;
        const usageMap: Record<string, UsageRollup> = {};
        usageResults.forEach(r => { if (r) usageMap[r.id] = r.u; });
        setUsageByTaskId(usageMap);

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

  // ── persist runPhase ──────────────────────────────────────────────────────────
  useEffect(() => {
    if (typeof window === 'undefined') return;
    try { localStorage.setItem(`tectika:board:${boardId}:runPhase`, JSON.stringify(runPhase)); } catch { /* quota */ }
  }, [boardId, runPhase]);

  // ── resolve the Run Board phase from live task/run state ──────────────────────
  // Authoritative reconciliation for the spinner: a batch task is still "live" while
  // its status is InProgress, or it has a run we haven't confirmed terminal yet
  // (including one not fetched). When nothing in the batch is live, the run has
  // settled — surface a status dot if we have run outcomes, otherwise return to idle.
  // This also self-heals a 'running' phase persisted from a prior session whose run
  // is long over, so the button never stays stuck spinning after a reload.
  useEffect(() => {
    if (runPhase.kind !== 'running' || loading) return;
    const batch = runPhase.taskIds
      .map(id => tasks.find(t => t.id === id))
      .filter((t): t is AgentTask => !!t);
    if (batch.length === 0) return; // tasks not loaded yet — wait

    const anyLive = batch.some(t =>
      t.status === 'InProgress' ||
      (!!t.workflowRunId && !runsById[t.workflowRunId]) || // run not fetched yet — wait
      (!!t.workflowRunId && !TERMINAL_RUN_STATUSES.has(runsById[t.workflowRunId]!.status)));
    if (anyLive) return;

    const runs = batch
      .map(t => (t.workflowRunId ? runsById[t.workflowRunId] : undefined))
      .filter((r): r is WorkflowRun => !!r);
    if (runs.length === 0) { setRunPhase({ kind: 'idle' }); return; } // nothing actually ran

    let status: 'AwaitingInteraction' | 'Failed' | 'Completed' = 'Completed';
    if (runs.some(r => r.status === 'AwaitingInteraction')) status = 'AwaitingInteraction';
    else if (runs.some(r => r.status === 'Failed' || r.status === 'Cancelled')) status = 'Failed';
    setRunPhase({ kind: 'done', status });
  }, [runPhase, tasks, runsById, loading]);

  // ── keep runsById fresh for active runs ───────────────────────────────────────
  // runsById is seeded once at load; poll any run that is missing or still executing
  // so per-task run state (and Run Board done-detection) reflects live status.
  const pendingRunKey = tasks
    .map(t => t.workflowRunId)
    .filter((r): r is string => !!r)
    .filter(rid => !TERMINAL_RUN_STATUSES.has(runsById[rid]?.status ?? ('' as RunStatus)))
    .join(',');
  useEffect(() => {
    if (loading || error || !pendingRunKey) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const ids = new Set(pendingRunKey.split(','));
    const targets = tasks.filter(t => t.workflowRunId && ids.has(t.workflowRunId));
    const tick = async () => {
      const fetched = await Promise.all(targets.map(t => api.runs.get(t.id, t.workflowRunId!).catch(() => null)));
      if (cancelled) return;
      const fresh = fetched.filter((r): r is WorkflowRun => !!r);
      if (fresh.length) {
        setRunsById(prev => { const next = { ...prev }; fresh.forEach(r => { next[r.id] = r; }); return next; });
      }
      if (!cancelled) timer = setTimeout(tick, 4000);
    };
    timer = setTimeout(tick, 1500);
    return () => { cancelled = true; if (timer) clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- pendingRunKey captures the relevant run/task identity
  }, [pendingRunKey, boardId, loading, error]);

  // ── live sync (PRD §2) ────────────────────────────────────────────────────────
  // Real-time updates via SSE on each active run, with a polling reconcile as a
  // resilient fallback. Server-authoritative status changes + newly-created tasks
  // flow in automatically; recent local edits are left untouched for 5s to avoid
  // clobbering optimistic changes.
  const activeRunKey = tasks.map(t => t.workflowRunId).filter(Boolean).join(',');
  useEffect(() => {
    if (loading || error) return;
    if (!liveEnabled) { setLiveState('paused'); return; }
    setLiveState('live');
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const runIds = [...new Set(tasks.map(t => t.workflowRunId).filter((r): r is string => !!r))];
    const unsubs = runIds.map(rid => api.streamRun(rid, (ev) => {
      if (!ev.taskId) return;
      if ((ev.type || '').toLowerCase().includes('status') && ev.content) {
        setTasks(prev => prev.map(t => t.id === ev.taskId ? { ...t, status: ev.content as AgentTaskStatus } : t));
      }
      // Any run event for a task may carry new usage — refresh its rollup so token/cost stay live.
      void refreshUsage(ev.taskId);
    }));

    const tick = async () => {
      if (cancelled) return;
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') { timer = setTimeout(tick, 7000); return; }
      try {
        const [fresh, freshEdges] = await Promise.all([
          api.tasks.list(boardId),
          api.edges.list(boardId).catch(() => null),
        ]);
        if (cancelled) return;
        setLiveState('live');
        if (Date.now() - lastEditRef.current > 5000) {
          setTasks(local => {
            const localIds = new Set(local.map(t => t.id));
            const freshById = new Map(fresh.map(t => [t.id, t]));
            const updated = local.map(t => {
              const srv = freshById.get(t.id);
              if (!srv) return t;
              // Adopt the server's status and any run it has attached (e.g. a run
              // started by another session/CLI, or one we haven't recorded locally)
              // so per-task and Run Board completion tracking can follow it.
              const statusChanged = srv.status !== t.status;
              const runChanged = !!srv.workflowRunId && srv.workflowRunId !== t.workflowRunId;
              if (!statusChanged && !runChanged) return t;
              return {
                ...t,
                ...(statusChanged ? { status: srv.status } : {}),
                ...(runChanged ? { workflowRunId: srv.workflowRunId } : {}),
              };
            });
            const added = fresh.filter(t => !localIds.has(t.id));
            return added.length ? [...updated, ...added] : updated;
          });
          if (freshEdges) setEdges(freshEdges);
        }
        // Reconcile usage for tasks that have a run (their tokens/cost can change as runs
        // progress or after a /clear). Resilient: a per-task failure is skipped.
        const usagePairs = await Promise.all(
          fresh.filter(x => x.workflowRunId).map(x =>
            api.usage.task(x.id).then(u => [x.id, u] as const).catch(() => null)),
        );
        if (!cancelled) {
          const usageUpdates = usagePairs.filter((p): p is readonly [string, UsageRollup] => !!p);
          if (usageUpdates.length) setUsageByTaskId(prev => {
            const next = { ...prev };
            usageUpdates.forEach(([id, u]) => { next[id] = u; });
            return next;
          });
        }
      } catch { if (!cancelled) setLiveState('reconnecting'); }
      if (!cancelled) timer = setTimeout(tick, 7000);
    };
    timer = setTimeout(tick, 7000);

    return () => { cancelled = true; if (timer) clearTimeout(timer); unsubs.forEach(u => u()); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [boardId, liveEnabled, loading, error, activeRunKey]);

  const toggleLive = useCallback(() => setLiveEnabled(v => !v), []);

  // ── derived ─────────────────────────────────────────────────────────────────
  const peopleById = useMemo(() => buildPeople(roles, tasks), [roles, tasks]);
  const people = useMemo(() => Object.values(peopleById), [peopleById]);
  const tasksById = useMemo(() => Object.fromEntries(tasks.map(t => [t.id, t])), [tasks]);

  // Derive the upstream/downstream relationships from Dependency edges only.
  const { upstreamIds, downstreamIds } = useMemo(() => {
    const up: Record<string, string[]> = {};
    const down: Record<string, string[]> = {};
    for (const e of edges) {
      if (e.kind !== 'Dependency') continue;
      (down[e.sourceTaskId] ??= []).push(e.targetTaskId);
      (up[e.targetTaskId] ??= []).push(e.sourceTaskId);
    }
    return { upstreamIds: up, downstreamIds: down };
  }, [edges]);

  const cellContext: CellContext = useMemo(
    () => ({ roles, peopleById, runsById, usageByTaskId, customCells: cfg.customCells, tasksById, upstreamIds, downstreamIds }),
    [roles, peopleById, runsById, usageByTaskId, cfg.customCells, tasksById, upstreamIds, downstreamIds],
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
    lastEditRef.current = Date.now();
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

  // Re-fetch a task from the server (e.g. after a slash-command mutates it out-of-band).
  // Refetch one task's usage rollup so token/cost (table column + panel) reflect runs and /clear.
  const refreshUsage = useCallback(async (id: string) => {
    try { const u = await api.usage.task(id); setUsageByTaskId(prev => ({ ...prev, [id]: u })); }
    catch { /* ignore — keep current */ }
  }, []);

  const refreshTask = useCallback(async (id: string) => {
    try { const t = await api.tasks.get(boardId, id); setTasks(prev => prev.map(x => x.id === id ? t : x)); }
    catch { /* ignore — keep current */ }
    void refreshUsage(id);   // e.g. after /clear (which resets the session bucket)
  }, [boardId, refreshUsage]);

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
    lastEditRef.current = Date.now();
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
    lastEditRef.current = Date.now();
    const set = new Set(ids);
    const removed = tasks.filter(t => set.has(t.id));
    let removedEdges: TaskEdge[] = [];
    setTasks(prev => prev.filter(t => !set.has(t.id)));
    setEdges(prev => {
      removedEdges = prev.filter(e => set.has(e.sourceTaskId) || set.has(e.targetTaskId));
      return prev.filter(e => !set.has(e.sourceTaskId) && !set.has(e.targetTaskId));
    });
    setSelectedIds([]);
    try {
      await Promise.all(ids.map(id => api.tasks.remove(boardId, id)));
      toast(`${ids.length} item${ids.length > 1 ? 's' : ''} deleted`, 'success');
    } catch {
      setTasks(prev => [...prev, ...removed]);
      setEdges(prev => [...prev, ...removedEdges]);
      toast('Could not delete items', 'error');
    }
  }, [boardId, tasks]);

  const moveCanvas = useCallback((id: string, x: number, y: number) => {
    setTasks(prev => prev.map(t => t.id === id ? { ...t, canvasPosition: { x, y } } : t));
    api.tasks.updatePosition(boardId, id, x, y).catch(() => {});
  }, [boardId]);

  // ── typed edges (server-persisted) ────────────────────────────────────────────
  const connectEdge = useCallback(async (source: string, target: string): Promise<TaskEdge | undefined> => {
    if (source === target) return undefined;
    lastEditRef.current = Date.now();
    try {
      const e = await api.edges.create(boardId, { sourceTaskId: source, targetTaskId: target });
      setEdges(p => [...p.filter(x => x.id !== e.id), e]);
      logActivity({ taskId: target, kind: 'connected', actorId: CURRENT_USER.id, from: source });
      return e;
    } catch { toast('Could not connect items', 'error'); return undefined; }
  }, [boardId, logActivity]);

  const disconnectEdge = useCallback(async (edgeId: string) => {
    lastEditRef.current = Date.now();
    let removed: TaskEdge | undefined;
    setEdges(p => { removed = p.find(e => e.id === edgeId); return p.filter(e => e.id !== edgeId); });
    try { await api.edges.remove(boardId, edgeId); }
    catch { if (removed) setEdges(p => p.some(e => e.id === removed!.id) ? p : [...p, removed!]); toast('Could not remove connection', 'error'); }
  }, [boardId]);

  const updateEdge = useCallback(async (edgeId: string, patch: Partial<Pick<TaskEdge, 'kind' | 'label' | 'maxIterations'>>) => {
    lastEditRef.current = Date.now();
    setEdges(p => p.map(e => e.id === edgeId ? { ...e, ...patch } : e));
    try { await api.edges.update(boardId, edgeId, patch); } catch { toast('Could not update edge', 'error'); }
  }, [boardId]);

  // Tasks eligible for a board run: agent-owned roots that are ready to make progress —
  // Backlog tasks (start fresh) and Failed tasks (reset to Backlog, then retry), mirroring
  // the per-task button, which offers Run on Backlog and Reset on a failed task. Tasks with
  // an upstream Dependency edge are excluded; the backend's dependency cascade kicks them off
  // as their inputs complete, so a board run only launches the roots.
  const runnable = useMemo(
    () => tasks.filter(t =>
      t.assignee?.type === 'Agent' &&
      (t.status === 'Backlog' || t.status === 'Failed') &&
      !edges.some(e => e.targetTaskId === t.id && e.kind === 'Dependency')),
    [tasks, edges],
  );
  const runnableTaskIds = useMemo(() => runnable.map(t => t.id), [runnable]);
  // The subset that needs a reset-to-Backlog before starting (drives the confirm dialog).
  const retryTaskIds = useMemo(
    () => runnable.filter(t => t.status === 'Failed').map(t => t.id),
    [runnable],
  );

  const runBoard = useCallback(async () => {
    if (runPhase.kind === 'running') return;
    const ids = runnableTaskIds;
    if (ids.length === 0) {
      toast('Nothing to run — all agent tasks are already running or complete.', 'info');
      return;
    }
    const retry = new Set(retryTaskIds);
    const batch = new Set(ids);
    setRunPhase({ kind: 'running', taskIds: ids });
    // Optimistically flip the whole batch to InProgress *before* awaiting the starts
    // (mirroring the backend, which flips on start). Without this, completion-detection
    // runs on the not-yet-started snapshot — sees no live tasks and no runs — and
    // immediately resolves the phase back to idle. Runs that fail to start are reverted below.
    setTasks(prev => prev.map(t => batch.has(t.id) ? { ...t, status: 'InProgress' } : t));
    // Failed tasks must be reset to Backlog before a fresh run (same as resetAndRun);
    // Backlog tasks start directly. Per-task failures are isolated by allSettled.
    const results = await Promise.allSettled(ids.map(async id => {
      if (retry.has(id)) await api.tasks.updateStatus(boardId, id, 'Backlog');
      return api.runs.start(boardId, id);
    }));
    // Record each started run on its task so the spinner reflects the run immediately
    // and completion detection can follow each run to a terminal state.
    const started = new Map<string, string>();
    results.forEach((res, i) => { if (res.status === 'fulfilled') started.set(ids[i], res.value.runId); });
    if (started.size > 0) {
      setTasks(prev => prev.map(t => started.has(t.id)
        ? { ...t, workflowRunId: started.get(t.id)! } : t));
    }
    if (started.size < ids.length) {
      // Revert the optimistic InProgress on tasks that never started, back to server truth.
      const failed = ids.filter(id => !started.has(id));
      await Promise.allSettled(failed.map(id => refreshTask(id)));
      toast(
        started.size === 0
          ? 'Could not start the board run.'
          : `Started ${started.size} of ${ids.length} tasks — the rest failed to start.`,
        'error',
      );
      if (started.size === 0) setRunPhase({ kind: 'idle' }); // nothing launched — don't strand the spinner
    }
  }, [boardId, runnableTaskIds, retryTaskIds, runPhase.kind, refreshTask]);

  const stopBoard = useCallback(async () => {
    if (runPhase.kind !== 'running') return;
    const ids = runPhase.taskIds;
    await Promise.allSettled(ids.map(id => api.tasks.stop(boardId, id)));
    await Promise.allSettled(ids.map(id => refreshTask(id)));
    setRunPhase({ kind: 'idle' });
  }, [boardId, runPhase, refreshTask]);

  // A task is "running" while its own status is InProgress ("Working on it").
  // Task status is the authoritative, live-synced signal the backend maps every
  // run state onto (e.g. PausedApproval→AwaitingApproval, NeedsRevision→Review),
  // so the button clears the moment the agent stops working — unlike the run
  // document, whose paused/revision states aren't terminal.
  const isTaskRunning = useCallback((task: AgentTask) => task.status === 'InProgress', []);

  const runTask = useCallback(async (taskId: string) => {
    const task = tasks.find(t => t.id === taskId);
    if (!task || task.assignee?.type !== 'Agent' || isTaskRunning(task)) return;
    try {
      const res = await api.runs.start(boardId, taskId);
      // mirror the backend, which flips the task to InProgress on start, so the
      // button reflects the run immediately and the live poll picks it up.
      setTasks(prev => prev.map(t => t.id === taskId ? { ...t, workflowRunId: res.runId, status: 'InProgress' } : t));
    } catch {
      toast('Could not start run', 'error');
    }
  }, [boardId, tasks, isTaskRunning]);

  const resetAndRun = useCallback(async (taskId: string) => {
    const task = tasks.find(t => t.id === taskId);
    if (!task || task.assignee?.type !== 'Agent') return;
    try {
      await api.tasks.updateStatus(boardId, taskId, 'Backlog');
      // "Reset" is a deliberate fresh start → begin a new usage session (current-session
      // tokens reset to zero; lifetime/board/project cost persist). Refresh usage right away
      // so the reset shows even if the run fails to start.
      await api.tasks.resetUsage(boardId, taskId).catch(() => {});
      void refreshUsage(taskId);
      const res = await api.runs.start(boardId, taskId);
      setTasks(prev => prev.map(t => t.id === taskId ? { ...t, workflowRunId: res.runId, status: 'InProgress' } : t));
      void refreshUsage(taskId);
    } catch {
      toast('Could not reset and run', 'error');
    }
  }, [boardId, tasks, refreshUsage]);

  const stopTask = useCallback(async (taskId: string) => {
    try {
      await api.tasks.stop(boardId, taskId);
      // backend restores the task's previous status on stop — refetch to reflect it.
      await refreshTask(taskId);
    } catch {
      toast('Could not stop the run', 'error');
    }
  }, [boardId, refreshTask]);

  const saveRole = useCallback(async (role: AgentRole) => {
    setRoles(prev => prev.map(r => r.id === role.id ? role : r));
    try { await api.agentRoles.upsert(role); } catch { toast('Could not save agent configuration', 'error'); }
  }, []);

  const pushChatTurns = useCallback((taskId: string, turns: ChatTurn[]) => {
    setCfg(prev => ({ ...prev, chatThreads: { ...prev.chatThreads, [taskId]: [...(prev.chatThreads[taskId] ?? []), ...turns] } }));
  }, []);

  const value: BoardContextValue = {
    loading, error, board, tasks, roles, runsById, usageByTaskId, peopleById, people, cellContext,
    views: cfg.views, activeView, columns: cfg.columns, visibleColumns,
    setActiveView, createView, updateActiveView, renameView, deleteView,
    setColumns, toggleColumnHidden, resizeColumn, addColumn, removeColumn, setAggregation,
    search, setSearch, setFilter, setSorts, setGroupBy,
    groups, visibleTasks,
    collapsedGroups: cfg.collapsedGroups, toggleGroup,
    selectedIds, toggleSelect, selectAll, clearSelection,
    updateTask, refreshTask, refreshUsage, setStatus, setCustomCell, addTask, deleteTasks, moveCanvas,
    edges, upstreamIds, downstreamIds, connectEdge, disconnectEdge, updateEdge,
    runBoard, stopBoard, runPhase, boardRunnableCount: runnableTaskIds.length, boardRetryCount: retryTaskIds.length, runTask, resetAndRun, stopTask, isTaskRunning,
    saveRole, chatThreads: cfg.chatThreads, pushChatTurns,
    liveEnabled, liveState, toggleLive,
    openTaskId, openTask: setOpenTaskId,
    repoChangesTarget,
    openRepoChanges: setRepoChangesTarget,
    clearRepoChangesTarget: () => setRepoChangesTarget(undefined),
    activity: cfg.activity, logActivity,
    automations: cfg.automations, saveAutomation, deleteAutomation, toggleAutomation,
  };

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
