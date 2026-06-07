'use client';

import React, { useEffect, useCallback, useMemo } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, addEdge,
  useNodesState, useEdgesState, Handle, Position,
  type Node, type Edge, type Connection, type NodeChange, type NodeProps, BackgroundVariant,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import Dagre from '@dagrejs/dagre';
import { useBoard } from '@/lib/board-context';
import type { AgentTask } from '@/lib/types';
import { STATUS_CONFIG, PRIORITY_CONFIG, colorFor } from '@/lib/palette';
import { Avatar, Pill } from '@/components/ui/primitives';
import { formatCompact } from '@/lib/format';

interface NodeData { taskId: string }

function layout(nodes: Node[], edges: Edge[]): Node[] {
  const g = new Dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'LR', nodesep: 50, ranksep: 110 });
  g.setDefaultEdgeLabel(() => ({}));
  nodes.forEach(n => g.setNode(n.id, { width: 230, height: 96 }));
  edges.forEach(e => g.setEdge(e.source, e.target));
  Dagre.layout(g);
  return nodes.map(n => { const p = g.node(n.id); return { ...n, position: { x: p.x - 115, y: p.y - 48 } }; });
}

function Inner() {
  const { tasks, roles, runsById, openTask, connectTasks, moveCanvas } = useBoard();
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const nodeTypes = useMemo(() => ({ agent: AgentNode }), []);

  useEffect(() => {
    const ns: Node[] = tasks.map(t => ({ id: t.id, type: 'agent', position: t.canvasPosition ?? { x: 0, y: 0 }, data: { taskId: t.id } }));
    const es: Edge[] = tasks.flatMap(t => t.downstreamTaskIds.filter(d => tasks.some(x => x.id === d)).map(d => ({
      id: `${t.id}->${d}`, source: t.id, target: d, animated: t.status === 'InProgress',
      style: { stroke: '#a25ddc', strokeWidth: 2 }, markerEnd: { type: 'arrowclosed' as never, color: '#a25ddc' },
    })));
    const hasPos = tasks.some(t => t.canvasPosition);
    setNodes(hasPos ? ns : layout(ns, es));
    setEdges(es);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tasks.length]);

  const onConnect = useCallback((c: Connection) => {
    setEdges(eds => addEdge({ ...c, animated: false, style: { stroke: '#a25ddc', strokeWidth: 2 } }, eds));
    if (c.source && c.target) connectTasks(c.source, c.target);
  }, [connectTasks, setEdges]);

  const handleNodesChange = useCallback((changes: NodeChange[]) => {
    onNodesChange(changes);
    changes.forEach(ch => { if (ch.type === 'position' && ch.position && !ch.dragging) moveCanvas(ch.id, ch.position.x, ch.position.y); });
  }, [onNodesChange, moveCanvas]);

  return (
    <BoardCanvasCtx.Provider value={{ tasks, roles, runsById, openTask }}>
      <ReactFlow nodes={nodes} edges={edges} onNodesChange={handleNodesChange} onEdgesChange={onEdgesChange} onConnect={onConnect}
        nodeTypes={nodeTypes} fitView fitViewOptions={{ padding: 0.2 }} className="bg-[var(--surface)]" proOptions={{ hideAttribution: true }}>
        <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="var(--muted-2)" />
        <Controls className="!bg-[var(--background)] !border !border-[var(--border)] !rounded-lg !shadow-sm" />
        <MiniMap pannable zoomable nodeColor={(n) => { const t = tasks.find(x => x.id === n.id); return t ? STATUS_CONFIG[t.status].hex : '#c4c4c4'; }} className="!bg-[var(--background)] !border !border-[var(--border)] !rounded-lg" />
      </ReactFlow>
    </BoardCanvasCtx.Provider>
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
    <div className="bg-[var(--background)] border-2 rounded-xl shadow-sm w-[230px] cursor-pointer hover:shadow-lg transition-all" style={{ borderColor: st.hex }}
      onDoubleClick={() => ctx?.openTask(task.id)}>
      <Handle type="target" position={Position.Left} className="!w-3 !h-3 !bg-[#0086c0] !border-2 !border-white" />
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
          {run && <span className="inline-flex items-center gap-1">⚡ {formatCompact(run.totalTokens)} tok</span>}
        </div>
      </div>
      <Handle type="source" position={Position.Right} className="!w-3 !h-3 !bg-[#00c875] !border-2 !border-white" />
    </div>
  );
}

export function CanvasView() {
  return <ReactFlowProvider><Inner /></ReactFlowProvider>;
}
