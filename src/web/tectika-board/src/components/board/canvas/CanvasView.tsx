'use client';

import React, { useEffect, useCallback, useMemo, useRef, useState, useContext } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, Panel,
  reconnectEdge, useNodesState, useEdgesState, useReactFlow, useStore,
  Handle, Position, BaseEdge, EdgeLabelRenderer, getBezierPath, MarkerType, BackgroundVariant,
  type Node, type Edge, type Connection, type NodeChange, type NodeProps, type EdgeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import Dagre from '@dagrejs/dagre';
import { useBoard } from '@/lib/board-context';
import type { AgentTask } from '@/lib/types';
import { STATUS_CONFIG, PRIORITY_CONFIG, colorFor } from '@/lib/palette';
import { Avatar, Pill } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { formatCompact } from '@/lib/format';
import { toast } from '@/lib/toast';
import { getFeedbackPath, type NodeBox } from './feedbackEdgePath';

interface NodeData { taskId: string }

const EDGE_COLOR = '#a25ddc';          // forward dependency
const EDGE_COLOR_HOT = '#7a3bbf';      // forward, hovered/selected
const FEEDBACK_COLOR = '#ff642e';      // feedback / error-routing (back-edge)
const FORWARD_MARKER = { type: MarkerType.ArrowClosed, color: EDGE_COLOR, width: 18, height: 18 } as const;
const FEEDBACK_MARKER = { type: MarkerType.ArrowClosed, color: FEEDBACK_COLOR, width: 18, height: 18 } as const;
const NO_NODES: Node[] = []; // stable ref: forward edges don't subscribe to node movement

function layout(nodes: Node[], edges: Edge[]): Node[] {
  const g = new Dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'LR', nodesep: 50, ranksep: 110 });
  g.setDefaultEdgeLabel(() => ({}));
  nodes.forEach(n => g.setNode(n.id, { width: 230, height: 96 }));
  edges.forEach(e => g.setEdge(e.source, e.target));
  Dagre.layout(g);
  return nodes.map(n => { const p = g.node(n.id); return { ...n, position: { x: p.x - 115, y: p.y - 48 } }; });
}

// Edge id is always `${source}->${target}` (equals TaskEdge.id).
function buildEdge(source: string, target: string, animated: boolean, feedback: boolean, label: string, currentIterations?: number, maxIterations?: number): Edge {
  return {
    id: `${source}->${target}`, source, target, type: 'pipeline', animated,
    data: { feedback, label, currentIterations, maxIterations },
    markerEnd: feedback ? FEEDBACK_MARKER : FORWARD_MARKER,
  };
}

const EdgeUiCtx = React.createContext<{
  hoveredId: string | null; setHovered: (id: string | null) => void;
  editingId: string | null; setEditingId: (id: string | null) => void;
  onDelete: (id: string) => void;
  saveLabel: (edgeId: string, label: string) => void;
  setFeedback: (edgeId: string, feedback: boolean) => void;
} | null>(null);

function Inner() {
  const { tasks, roles, runsById, openTask, edges: taskEdges, connectEdge, disconnectEdge, updateEdge, moveCanvas } = useBoard();
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [hoveredId, setHovered] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const { deleteElements } = useReactFlow();
  const reconnectDone = useRef(true);

  const nodeTypes = useMemo(() => ({ agent: AgentNode }), []);
  const edgeTypes = useMemo(() => ({ pipeline: PipelineEdge }), []);
  const onDelete = useCallback((id: string) => { deleteElements({ edges: [{ id }] }); }, [deleteElements]);
  const saveLabel = useCallback((edgeId: string, label: string) => { updateEdge(edgeId, { label: label.trim() }); }, [updateEdge]);
  const setFeedback = useCallback((edgeId: string, feedback: boolean) => { updateEdge(edgeId, { kind: feedback ? 'QaFeedback' : 'Dependency' }); }, [updateEdge]);
  const edgeUi = useMemo(() => ({ hoveredId, setHovered, editingId, setEditingId, onDelete, saveLabel, setFeedback }), [hoveredId, editingId, onDelete, saveLabel, setFeedback]);

  // Rebuild canvas nodes/edges from server state (tasks + typed edges).
  // Depend on full `tasks` (not just tasks.length) so status changes re-derive animated-edge flags.
  const statusById = useMemo(() => Object.fromEntries(tasks.map(t => [t.id, t.status])), [tasks]);
  const taskIds = useMemo(() => new Set(tasks.map(t => t.id)), [tasks]);
  useEffect(() => {
    const es: Edge[] = taskEdges
      .filter(e => taskIds.has(e.sourceTaskId) && taskIds.has(e.targetTaskId))
      .map(e => buildEdge(e.sourceTaskId, e.targetTaskId, statusById[e.sourceTaskId] === 'InProgress', e.kind === 'QaFeedback', e.label ?? '', e.currentIterations, e.maxIterations));
    setNodes(prev => {
      // Preserve existing node positions (from drag) unless the task has an explicit canvasPosition.
      const prevById = new Map(prev.map(n => [n.id, n]));
      const ns: Node[] = tasks.map(t => {
        const existing = prevById.get(t.id);
        const position = t.canvasPosition ?? existing?.position ?? { x: 0, y: 0 };
        // Reuse the existing node object when nothing changed so React Flow doesn't
        // re-adopt it (which transiently drops edge positions and remounts edges).
        if (existing && existing.position.x === position.x && existing.position.y === position.y) return existing;
        return { id: t.id, type: 'agent', position, data: existing?.data ?? { taskId: t.id } };
      });
      const hasPos = tasks.some(t => t.canvasPosition) || prev.length > 0;
      return hasPos ? ns : layout(ns, es);
    });
    setEdges(es);
  }, [tasks, taskEdges, taskIds, statusById, setNodes, setEdges]);

  // Feedback loops are allowed (PRD); only block self-links and exact duplicates.
  const isValidConnection = useCallback((c: Connection | Edge) => {
    if (!c.source || !c.target || c.source === c.target) return false;
    return !edges.some(e => e.source === c.source && e.target === c.target);
  }, [edges]);

  const onConnect = useCallback(async (c: Connection) => {
    if (!c.source || !c.target) return;
    if (c.source === c.target) { toast("A task can't depend on itself", "info"); return; }
    if (edges.some(e => e.source === c.source && e.target === c.target)) { toast("These tasks are already linked", "info"); return; }
    // Add an optimistic canvas edge immediately so the link appears without waiting for the API.
    const optimisticId = `${c.source}->${c.target}`;
    setEdges(prev => prev.some(e => e.id === optimisticId) ? prev : [...prev, buildEdge(c.source!, c.target!, false, false, '')]);
    const created = await connectEdge(c.source, c.target);
    // The context taskEdges change will reconcile/replace the optimistic edge (same id).
    // Reflect feedback styling + nudge labelling if the server detected a feedback loop.
    if (created?.kind === 'QaFeedback') {
      toast("Feedback loop created — double-click it to label the route", "info");
      setEditingId(created.id);
    }
  }, [edges, connectEdge, setEdges]);

  // Reconnection: drag an edge endpoint onto another node; carry the label across.
  const onReconnectStart = useCallback(() => { reconnectDone.current = false; }, []);
  const onReconnect = useCallback(async (oldEdge: Edge, c: Connection) => {
    const unchanged = c.source === oldEdge.source && c.target === oldEdge.target;
    if (!unchanged && !isValidConnection(c)) { toast("Can't move the link there", 'error'); return; }
    reconnectDone.current = true;
    if (unchanged || !c.source || !c.target) { setEdges(els => reconnectEdge(oldEdge, c, els)); return; }
    const label = (oldEdge.data as { label?: string })?.label;
    await disconnectEdge(oldEdge.id);
    const created = await connectEdge(c.source, c.target);
    if (created && label) updateEdge(created.id, { label });
  }, [isValidConnection, setEdges, disconnectEdge, connectEdge, updateEdge]);
  const onReconnectEnd = useCallback((_: unknown, edge: Edge) => {
    if (!reconnectDone.current) onDelete(edge.id);
    reconnectDone.current = true;
  }, [onDelete]);

  const onEdgesDelete = useCallback((deleted: Edge[]) => {
    deleted.forEach(e => { disconnectEdge(e.id); });
  }, [disconnectEdge]);

  const handleNodesChange = useCallback((changes: NodeChange[]) => {
    onNodesChange(changes);
    changes.forEach(ch => { if (ch.type === 'position' && ch.position && !ch.dragging) moveCanvas(ch.id, ch.position.x, ch.position.y); });
  }, [onNodesChange, moveCanvas]);

  return (
    <BoardCanvasCtx.Provider value={{ tasks, roles, runsById, openTask }}>
    <EdgeUiCtx.Provider value={edgeUi}>
      <ReactFlow
        nodes={nodes} edges={edges}
        onNodesChange={handleNodesChange} onEdgesChange={onEdgesChange}
        onConnect={onConnect} isValidConnection={isValidConnection}
        onReconnect={onReconnect} onReconnectStart={onReconnectStart} onReconnectEnd={onReconnectEnd}
        onEdgesDelete={onEdgesDelete}
        onEdgeMouseEnter={(_, e) => setHovered(e.id)} onEdgeMouseLeave={() => setHovered(null)}
        onEdgeDoubleClick={(_, e) => setEditingId(e.id)}
        nodeTypes={nodeTypes} edgeTypes={edgeTypes}
        deleteKeyCode={['Delete', 'Backspace']}
        connectionRadius={34}
        connectionLineStyle={{ stroke: EDGE_COLOR, strokeWidth: 2.5, strokeDasharray: '6 4' }}
        defaultEdgeOptions={{ type: 'pipeline', markerEnd: FORWARD_MARKER }}
        fitView fitViewOptions={{ padding: 0.2 }}
        className="bg-[var(--surface)]" proOptions={{ hideAttribution: true }}
      >
        <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="var(--muted-2)" />
        <Controls className="!bg-[var(--background)] !border !border-[var(--border)] !rounded-lg !shadow-sm" />
        <MiniMap pannable zoomable nodeColor={(n) => { const t = tasks.find(x => x.id === n.id); return t ? STATUS_CONFIG[t.status].hex : '#c4c4c4'; }} className="!bg-[var(--background)] !border !border-[var(--border)] !rounded-lg" />
        <Panel position="top-left">
          <div className="flex flex-col gap-1 bg-[var(--background)]/95 backdrop-blur border border-[var(--border)] rounded-lg shadow-sm px-3 py-2 text-[11px] text-[var(--muted)] max-w-[280px]">
            <span className="font-semibold text-[var(--foreground)] flex items-center gap-1.5"><Icon.flow size={13} /> Agent pipeline</span>
            <span>Drag <span className="text-[#00c875] font-medium">out ▸</span> to <span className="text-[#0086c0] font-medium">▸ in</span> to link tasks.</span>
            <span className="flex items-center gap-1"><span className="inline-block w-4 border-t-2 border-dashed" style={{ borderColor: FEEDBACK_COLOR }} /> A link back to an earlier task is a <span style={{ color: FEEDBACK_COLOR }} className="font-medium">feedback loop</span>.</span>
            <span><b className="text-[var(--foreground)]">Double-click</b> a link to label it · hover for edit / <span className="font-medium text-[var(--foreground)]">×</span> · drag an end to reconnect.</span>
          </div>
        </Panel>
      </ReactFlow>
    </EdgeUiCtx.Provider>
    </BoardCanvasCtx.Provider>
  );
}

// ── Custom edge: feedback styling, hover/selection highlight, label + toolbar ────
function PipelineEdge({ id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, markerEnd, selected, data }: EdgeProps) {
  const ui = useContext(EdgeUiCtx);
  const feedback = !!(data as { feedback?: boolean })?.feedback;
  // Only feedback edges need live node boxes (to dip below obstacles). Forward edges
  // select a stable empty array so they don't re-render on every node drag.
  const nodes = useStore((s) => (feedback ? s.nodes : NO_NODES));
  const nodeBoxes = useMemo<NodeBox[]>(
    () => nodes.map((n) => ({
      id: n.id,
      x: n.position.x,
      y: n.position.y,
      width: n.measured?.width ?? 0,
      height: n.measured?.height ?? 0,
    })),
    [nodes],
  );
  const [path, labelX, labelY] = feedback
    ? getFeedbackPath({ sourceX, sourceY, targetX, targetY, sourceId: source, targetId: target, nodeBoxes })
    : getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  const label = ((data as { label?: string })?.label) ?? '';
  const currentIterations = (data as { currentIterations?: number })?.currentIterations;
  const maxIterations = (data as { maxIterations?: number })?.maxIterations;
  const editing = ui?.editingId === id;
  const hovered = ui?.hoveredId === id;
  const show = !!selected || hovered || editing;
  const baseColor = feedback ? FEEDBACK_COLOR : EDGE_COLOR;
  const stroke = feedback ? FEEDBACK_COLOR : (show ? EDGE_COLOR_HOT : EDGE_COLOR);
  return (
    <>
      <BaseEdge id={id} path={path} markerEnd={markerEnd} interactionWidth={26}
        style={{ stroke, strokeWidth: show ? 3 : 2, strokeDasharray: feedback ? '7 5' : undefined, transition: 'stroke 0.1s, stroke-width 0.1s' }} />
      <EdgeLabelRenderer>
        <div
          className="nodrag nopan absolute"
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
          onMouseEnter={() => ui?.setHovered(id)} onMouseLeave={() => ui?.setHovered(null)}
        >
          {editing ? (
            <EdgeLabelInput initial={label} color={baseColor} feedback={feedback}
              onSave={(v) => { ui?.saveLabel(id, v); ui?.setEditingId(null); }}
              onToggleFeedback={(checked) => ui?.setFeedback(id, checked)}
              onCancel={() => ui?.setEditingId(null)} />
          ) : (
            <div className="flex items-center gap-1" style={{ pointerEvents: show || label || feedback ? 'all' : 'none' }}>
              {(label || feedback) && (
                <button
                  onClick={() => ui?.setEditingId(id)}
                  className="flex items-center gap-1 px-1.5 py-0.5 rounded-md text-[10px] font-semibold shadow-sm border max-w-[160px] truncate"
                  style={{ background: 'var(--background)', borderColor: baseColor + '66', color: baseColor }}
                  title="Edit label"
                >
                  {feedback && <Icon.refresh size={10} />}
                  <span className="truncate">{label || 'add label…'}</span>
                </button>
              )}
              {feedback && maxIterations != null && maxIterations > 0 && (
                <span
                  className="inline-flex items-center gap-0.5 px-1.5 py-0.5 rounded text-[10px] font-bold border"
                  style={{ borderColor: '#ff642e55', color: '#ff642e', background: 'var(--background)' }}
                  title={`QA iterations: ${currentIterations ?? 0} of ${maxIterations}`}
                >
                  ↻ {currentIterations ?? 0}/{maxIterations}
                </span>
              )}
              {show && (
                <button
                  title="Remove link"
                  onClick={(e) => { e.stopPropagation(); ui?.onDelete(id); }}
                  className="w-5 h-5 flex items-center justify-center rounded-full bg-[var(--background)] border border-[var(--border)] shadow text-[var(--muted)] hover:bg-[#e2445c] hover:text-white hover:border-[#e2445c] transition-colors"
                >
                  <Icon.x size={12} />
                </button>
              )}
            </div>
          )}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}

function EdgeLabelInput({ initial, color, feedback, onSave, onToggleFeedback, onCancel }: {
  initial: string; color: string; feedback: boolean;
  onSave: (v: string) => void; onToggleFeedback: (checked: boolean) => void; onCancel: () => void;
}) {
  const [v, setV] = useState(initial);
  return (
    <div className="flex flex-col gap-1.5 p-2 rounded-md shadow-md bg-[var(--background)]" style={{ border: `1.5px solid ${color}` }}>
      <input
        autoFocus value={v}
        onChange={e => setV(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter') onSave(v); if (e.key === 'Escape') onCancel(); e.stopPropagation(); }}
        placeholder="e.g. if QA fails, revise"
        className="px-2 py-1 rounded text-[11px] font-medium outline-none bg-[var(--surface)] text-[var(--foreground)] w-[180px]"
        style={{ border: `1px solid ${color}55` }}
      />
      <label className="flex items-center gap-1.5 text-[10px] font-medium text-[var(--muted)] cursor-pointer select-none">
        <input type="checkbox" checked={feedback} onChange={e => onToggleFeedback(e.target.checked)} className="accent-[#ff642e]" />
        Feedback loop
      </label>
    </div>
  );
}

// lightweight context so the custom node can read data without prop plumbing through ReactFlow
const BoardCanvasCtx = React.createContext<{ tasks: AgentTask[]; roles: ReturnType<typeof useBoard>['roles']; runsById: ReturnType<typeof useBoard>['runsById']; openTask: (id: string) => void } | null>(null);

function AgentNode({ data }: NodeProps) {
  const ctx = React.useContext(BoardCanvasCtx);
  const { taskId } = data as unknown as NodeData;
  const task = ctx?.tasks.find(t => t.id === taskId);
  if (!task) return null;
  const role = ctx?.roles.find(r => r.id === task.assignee.id);
  const run = task.workflowRunId ? ctx?.runsById[task.workflowRunId] : undefined;
  const st = STATUS_CONFIG[task.status];
  return (
    <div className="group/node bg-[var(--background)] border-2 rounded-xl shadow-sm w-[230px] cursor-pointer hover:shadow-lg transition-all" style={{ borderColor: st.hex }}
      onDoubleClick={() => ctx?.openTask(task.id)}>
      <Handle type="target" position={Position.Left} title="Incoming — drop a link here"
        className="!w-3.5 !h-3.5 !bg-[#0086c0] !border-2 !border-white !shadow group-hover/node:!scale-110 transition-transform" />
      <div className="h-1.5 rounded-t-[10px]" style={{ background: st.hex }} />
      <div className="p-3">
        <div className="flex items-center justify-between mb-1.5">
          <div className="flex items-center gap-1.5 min-w-0">
            <Avatar name={role?.displayName ?? task.assignee.id} hex={role ? colorFor(role.id) : undefined} size={20} person={role ? { id: role.id, name: role.displayName, kind: 'Agent', hex: colorFor(role.id) } : undefined} />
            <span className="text-[11px] text-[var(--muted)] truncate">{role?.displayName ?? task.assignee.id}</span>
          </div>
          <Pill label={st.label} hex={st.hex} />
        </div>
        <p className="font-medium text-[13px] leading-tight text-[var(--foreground)] line-clamp-2 mb-1.5">{task.title}</p>
        <div className="flex items-center justify-between text-[10px] text-[var(--muted)]">
          <span className="px-1.5 py-0.5 rounded" style={{ background: `${PRIORITY_CONFIG[task.priority].hex}22`, color: PRIORITY_CONFIG[task.priority].hex }}>{task.priority}</span>
          {run && <span className="inline-flex items-center gap-1"><Icon.bolt size={11} /> {formatCompact(run.totalTokens)} tok</span>}
        </div>
      </div>
      <Handle type="source" position={Position.Right} title="Outgoing — drag from here to link"
        className="!w-3.5 !h-3.5 !bg-[#00c875] !border-2 !border-white !shadow group-hover/node:!scale-110 transition-transform" />
    </div>
  );
}

export function CanvasView() {
  return <ReactFlowProvider><Inner /></ReactFlowProvider>;
}
