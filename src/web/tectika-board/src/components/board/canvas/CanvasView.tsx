'use client';

import React, { useEffect, useCallback, useMemo, useRef, useState, useContext } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, Panel,
  addEdge, reconnectEdge, useNodesState, useEdgesState, useReactFlow,
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

interface NodeData { taskId: string }

const EDGE_COLOR = '#a25ddc';          // forward dependency
const EDGE_COLOR_HOT = '#7a3bbf';      // forward, hovered/selected
const FEEDBACK_COLOR = '#ff642e';      // feedback / error-routing (back-edge)
const FORWARD_MARKER = { type: MarkerType.ArrowClosed, color: EDGE_COLOR, width: 18, height: 18 } as const;
const FEEDBACK_MARKER = { type: MarkerType.ArrowClosed, color: FEEDBACK_COLOR, width: 18, height: 18 } as const;

function layout(nodes: Node[], edges: Edge[]): Node[] {
  const g = new Dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'LR', nodesep: 50, ranksep: 110 });
  g.setDefaultEdgeLabel(() => ({}));
  nodes.forEach(n => g.setNode(n.id, { width: 230, height: 96 }));
  edges.forEach(e => g.setEdge(e.source, e.target));
  Dagre.layout(g);
  return nodes.map(n => { const p = g.node(n.id); return { ...n, position: { x: p.x - 115, y: p.y - 48 } }; });
}

// Edge id is always `${source}->${target}`, which doubles as the edge-label key.
function buildEdge(source: string, target: string, animated: boolean): Edge {
  return { id: `${source}->${target}`, source, target, type: 'pipeline', animated, markerEnd: FORWARD_MARKER };
}

function reachable(edges: Edge[], from: string, to: string): boolean {
  const adj = new Map<string, string[]>();
  for (const e of edges) (adj.get(e.source) ?? adj.set(e.source, []).get(e.source)!).push(e.target);
  const stack = [from]; const seen = new Set<string>();
  while (stack.length) {
    const cur = stack.pop()!;
    if (cur === to) return true;
    if (seen.has(cur)) continue;
    seen.add(cur);
    for (const n of adj.get(cur) ?? []) stack.push(n);
  }
  return false;
}
/**
 * Classify the minimal set of "feedback" (back) edges via DFS — an edge is a back
 * edge only when it points to a node currently on the recursion stack (an ancestor).
 * In a cycle this flags just the one edge that closes the loop, not every edge in it.
 */
function feedbackEdgeIds(edges: Edge[]): Set<string> {
  const adj = new Map<string, { to: string; id: string }[]>();
  const nodes: string[] = [];
  for (const e of edges) {
    if (!adj.has(e.source)) { adj.set(e.source, []); nodes.push(e.source); }
    if (!adj.has(e.target)) { adj.set(e.target, []); nodes.push(e.target); }
    adj.get(e.source)!.push({ to: e.target, id: e.id });
  }
  const WHITE = 0, GRAY = 1, BLACK = 2;
  const color = new Map<string, number>(nodes.map(n => [n, WHITE]));
  const back = new Set<string>();
  const visit = (start: string) => {
    const stack: Array<{ u: string; i: number }> = [{ u: start, i: 0 }];
    color.set(start, GRAY);
    while (stack.length) {
      const top = stack[stack.length - 1];
      const out = adj.get(top.u)!;
      if (top.i < out.length) {
        const { to, id } = out[top.i++];
        const c = color.get(to);
        if (c === GRAY) back.add(id);             // edge to an ancestor on the stack
        else if (c === WHITE) { color.set(to, GRAY); stack.push({ u: to, i: 0 }); }
      } else { color.set(top.u, BLACK); stack.pop(); }
    }
  };
  for (const n of nodes) if (color.get(n) === WHITE) visit(n);
  return back;
}

const EdgeUiCtx = React.createContext<{
  hoveredId: string | null; setHovered: (id: string | null) => void;
  editingId: string | null; setEditingId: (id: string | null) => void;
  onDelete: (id: string) => void;
  saveLabel: (source: string, target: string, label: string) => void;
} | null>(null);

function Inner() {
  const { tasks, roles, runsById, openTask, connectTasks, disconnectTasks, moveCanvas, edgeLabels, setEdgeLabel } = useBoard();
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [hoveredId, setHovered] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const { deleteElements } = useReactFlow();
  const reconnectDone = useRef(true);

  const nodeTypes = useMemo(() => ({ agent: AgentNode }), []);
  const edgeTypes = useMemo(() => ({ pipeline: PipelineEdge }), []);
  const onDelete = useCallback((id: string) => { deleteElements({ edges: [{ id }] }); }, [deleteElements]);
  const edgeUi = useMemo(() => ({ hoveredId, setHovered, editingId, setEditingId, onDelete, saveLabel: setEdgeLabel }), [hoveredId, editingId, onDelete, setEdgeLabel]);

  useEffect(() => {
    const ns: Node[] = tasks.map(t => ({ id: t.id, type: 'agent', position: t.canvasPosition ?? { x: 0, y: 0 }, data: { taskId: t.id } }));
    const es: Edge[] = tasks.flatMap(t => t.downstreamTaskIds.filter(d => tasks.some(x => x.id === d)).map(d => buildEdge(t.id, d, t.status === 'InProgress')));
    const hasPos = tasks.some(t => t.canvasPosition);
    setNodes(hasPos ? ns : layout(ns, es));
    setEdges(es);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tasks.length]);

  // Derived display edges: classify feedback (back) edges, colour them, attach labels.
  const styledEdges = useMemo(() => {
    const feedbackSet = feedbackEdgeIds(edges);
    return edges.map(e => {
      const feedback = feedbackSet.has(e.id);
      return { ...e, data: { ...e.data, feedback, label: edgeLabels[e.id] ?? '' }, markerEnd: feedback ? FEEDBACK_MARKER : FORWARD_MARKER };
    });
  }, [edges, edgeLabels]);

  // Feedback loops are allowed (PRD); only block self-links and exact duplicates.
  const isValidConnection = useCallback((c: Connection | Edge) => {
    if (!c.source || !c.target || c.source === c.target) return false;
    return !edges.some(e => e.source === c.source && e.target === c.target);
  }, [edges]);

  const onConnect = useCallback((c: Connection) => {
    if (!c.source || !c.target) return;
    if (c.source === c.target) { toast('A task can’t depend on itself', 'info'); return; }
    if (edges.some(e => e.source === c.source && e.target === c.target)) { toast('These tasks are already linked', 'info'); return; }
    setEdges(eds => addEdge(buildEdge(c.source!, c.target!, false), eds));
    connectTasks(c.source, c.target);
    if (reachable(edges, c.target, c.source)) {
      toast('Feedback loop created — double-click it to label the route', 'info');
      setEditingId(`${c.source}->${c.target}`);
    }
  }, [edges, connectTasks, setEdges]);

  // Reconnection: drag an edge endpoint onto another node; carry the label across.
  const onReconnectStart = useCallback(() => { reconnectDone.current = false; }, []);
  const onReconnect = useCallback((oldEdge: Edge, c: Connection) => {
    const unchanged = c.source === oldEdge.source && c.target === oldEdge.target;
    if (!unchanged && !isValidConnection(c)) { toast('Can’t move the link there', 'error'); return; }
    reconnectDone.current = true;
    if (unchanged || !c.source || !c.target) { setEdges(els => reconnectEdge(oldEdge, c, els)); return; }
    const label = edgeLabels[oldEdge.id];
    setEdges(els => els.filter(e => e.id !== oldEdge.id).concat(buildEdge(c.source!, c.target!, false)));
    disconnectTasks(oldEdge.source, oldEdge.target);
    setEdgeLabel(oldEdge.source, oldEdge.target, '');
    connectTasks(c.source, c.target);
    if (label) setEdgeLabel(c.source, c.target, label);
  }, [isValidConnection, setEdges, disconnectTasks, connectTasks, edgeLabels, setEdgeLabel]);
  const onReconnectEnd = useCallback((_: unknown, edge: Edge) => {
    if (!reconnectDone.current) onDelete(edge.id);
    reconnectDone.current = true;
  }, [onDelete]);

  const onEdgesDelete = useCallback((deleted: Edge[]) => {
    deleted.forEach(e => { disconnectTasks(e.source, e.target); setEdgeLabel(e.source, e.target, ''); });
  }, [disconnectTasks, setEdgeLabel]);

  const handleNodesChange = useCallback((changes: NodeChange[]) => {
    onNodesChange(changes);
    changes.forEach(ch => { if (ch.type === 'position' && ch.position && !ch.dragging) moveCanvas(ch.id, ch.position.x, ch.position.y); });
  }, [onNodesChange, moveCanvas]);

  return (
    <BoardCanvasCtx.Provider value={{ tasks, roles, runsById, openTask }}>
    <EdgeUiCtx.Provider value={edgeUi}>
      <ReactFlow
        nodes={nodes} edges={styledEdges}
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
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  const feedback = !!(data as { feedback?: boolean })?.feedback;
  const label = ((data as { label?: string })?.label) ?? '';
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
            <EdgeLabelInput initial={label} color={baseColor}
              onSave={(v) => { ui?.saveLabel(source, target, v); ui?.setEditingId(null); }}
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

function EdgeLabelInput({ initial, color, onSave, onCancel }: { initial: string; color: string; onSave: (v: string) => void; onCancel: () => void }) {
  const [v, setV] = useState(initial);
  return (
    <input
      autoFocus value={v}
      onChange={e => setV(e.target.value)}
      onBlur={() => onSave(v)}
      onKeyDown={e => { if (e.key === 'Enter') onSave(v); if (e.key === 'Escape') onCancel(); e.stopPropagation(); }}
      placeholder="e.g. if QA fails, revise"
      className="px-2 py-1 rounded-md text-[11px] font-medium shadow-md outline-none bg-[var(--background)] text-[var(--foreground)] w-[180px]"
      style={{ border: `1.5px solid ${color}` }}
    />
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
