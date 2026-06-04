'use client';

import { useEffect, useState, useCallback } from 'react';
import { useParams } from 'next/navigation';
import Link from 'next/link';
import {
  ReactFlow,
  ReactFlowProvider,
  Background,
  Controls,
  MiniMap,
  addEdge,
  useNodesState,
  useEdgesState,
  type Node,
  type Edge,
  type Connection,
  type NodeChange,
  type EdgeChange,
  BackgroundVariant,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import Dagre from '@dagrejs/dagre';
import { api } from '@/lib/api';
import { AgentNode } from '@/components/canvas/AgentNode';
import type { AgentTask, AgentRole } from '@/lib/types';

const NODE_TYPES = { agentNode: AgentNode };

function layoutWithDagre(nodes: Node[], edges: Edge[]): Node[] {
  const g = new Dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'LR', nodesep: 60, ranksep: 100 });
  g.setDefaultEdgeLabel(() => ({}));

  nodes.forEach(n => g.setNode(n.id, { width: 224, height: 88 }));
  edges.forEach(e => g.setEdge(e.source, e.target));
  Dagre.layout(g);

  return nodes.map(n => {
    const pos = g.node(n.id);
    return { ...n, position: { x: pos.x - 112, y: pos.y - 44 } };
  });
}

function buildGraph(tasks: AgentTask[], roles: AgentRole[], onOpen: (id: string) => void) {
  const nodes: Node[] = tasks.map(t => ({
    id: t.id,
    type: 'agentNode',
    position: t.canvasPosition ?? { x: 0, y: 0 },
    data: {
      task: t,
      role: roles.find(r => r.id === t.assignee.id),
      onOpen,
    },
  }));

  const edges: Edge[] = tasks.flatMap(t =>
    t.downstreamTaskIds.map(downId => ({
      id: `${t.id}->${downId}`,
      source: t.id,
      target: downId,
      animated: t.status === 'InProgress',
      style: { stroke: '#0073ea', strokeWidth: 2 },
    }))
  );

  const hasPositions = tasks.some(t => t.canvasPosition);
  return {
    nodes: hasPositions ? nodes : layoutWithDagre(nodes, edges),
    edges,
  };
}

function CanvasInner({ boardId }: { boardId: string }) {
  const [tasks, setTasks] = useState<AgentTask[]>([]);
  const [roles, setRoles] = useState<AgentRole[]>([]);
  const [boardName, setBoardName] = useState('');
  const [loading, setLoading] = useState(true);

  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);

  const handleOpen = useCallback((taskId: string) => {
    const task = tasks.find(t => t.id === taskId);
    if (task) window.open(`/workspace/${boardId}/${taskId}`, '_blank');
  }, [tasks, boardId]);

  useEffect(() => {
    if (!boardId) return;
    Promise.all([
      api.boards.list().then(bs => bs.find(b => b.id === boardId)),
      api.tasks.list(boardId),
      api.agentRoles.list(),
    ])
      .then(([b, t, r]) => {
        setBoardName(b?.name ?? 'Board');
        setTasks(t);
        setRoles(r);
        const { nodes: n, edges: e } = buildGraph(t, r, handleOpen);
        setNodes(n);
        setEdges(e);
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, [boardId, handleOpen, setNodes, setEdges]);

  const onConnect = useCallback(async (connection: Connection) => {
    setEdges(eds => addEdge({
      ...connection,
      animated: false,
      style: { stroke: '#0073ea', strokeWidth: 2 },
    }, eds));
    if (connection.source && connection.target) {
      await api.tasks.connect(boardId, connection.source, connection.target).catch(() => {});
    }
  }, [boardId, setEdges]);

  const handleNodesChange = useCallback((changes: NodeChange[]) => {
    onNodesChange(changes);
    changes.forEach(change => {
      if (change.type === 'position' && change.position && !change.dragging) {
        const task = tasks.find(t => t.id === change.id);
        if (task && change.position) {
          api.tasks.updatePosition(boardId, change.id, change.position.x, change.position.y).catch(() => {});
        }
      }
    });
  }, [boardId, tasks, onNodesChange]);

  if (loading) {
    return (
      <div className="flex items-center justify-center h-full text-[#676879] text-sm gap-2">
        <div className="w-4 h-4 border-2 border-[#0073ea] border-t-transparent rounded-full animate-spin" />
        Loading canvas…
      </div>
    );
  }

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      onNodesChange={handleNodesChange}
      onEdgesChange={onEdgesChange}
      onConnect={onConnect}
      nodeTypes={NODE_TYPES}
      fitView
      fitViewOptions={{ padding: 0.2 }}
      className="bg-[#f8f9fc]"
    >
      <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="#d0d4e0" />
      <Controls className="bg-white border border-[#e6e9ef] rounded-lg shadow-sm" />
      <MiniMap
        nodeColor={n => {
          const task = tasks.find(t => t.id === n.id);
          if (!task) return '#c4c4c4';
          const map: Record<string, string> = {
            Done: '#00c875', InProgress: '#fdab3d', Failed: '#e2445c',
            AwaitingApproval: '#a25ddc', Blocked: '#ff642e', Review: '#66ccff',
          };
          return map[task.status] ?? '#c4c4c4';
        }}
        className="bg-white border border-[#e6e9ef] rounded-lg shadow-sm"
      />

      {/* Overlay header */}
      <div className="absolute top-4 left-4 z-10 flex items-center gap-3 bg-white rounded-xl px-4 py-2.5 shadow-lg border border-[#e6e9ef]">
        <Link
          href={`/boards/${boardId}`}
          className="text-[#676879] hover:text-[#323338] transition-colors"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
            <path d="M19 12H5M12 19l-7-7 7-7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        </Link>
        <span className="text-sm font-semibold text-[#323338]">{boardName}</span>
        <span className="text-xs text-[#676879] bg-[#f5f6f8] px-2 py-0.5 rounded-full">Flow Canvas</span>
        <span className="text-[10px] text-[#c3c6d4]">
          {tasks.length} task{tasks.length !== 1 ? 's' : ''}
        </span>
      </div>

      {/* Legend */}
      <div className="absolute bottom-4 left-4 z-10 bg-white rounded-xl px-3 py-2 shadow-lg border border-[#e6e9ef] flex flex-col gap-1">
        <p className="text-[9px] uppercase tracking-widest font-semibold text-[#676879] mb-0.5">Legend</p>
        {[
          { label: 'Working', color: '#fdab3d' },
          { label: 'Done', color: '#00c875' },
          { label: 'Blocked', color: '#ff642e' },
          { label: 'Approval', color: '#a25ddc' },
        ].map(({ label, color }) => (
          <div key={label} className="flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-sm" style={{ background: color }} />
            <span className="text-[10px] text-[#676879]">{label}</span>
          </div>
        ))}
      </div>
    </ReactFlow>
  );
}

export default function CanvasPage() {
  const params = useParams();
  const boardId = params.boardId as string;

  return (
    <div className="flex flex-col h-full">
      <ReactFlowProvider>
        <CanvasInner boardId={boardId} />
      </ReactFlowProvider>
    </div>
  );
}
