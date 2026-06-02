// API client — כל calls ל-.NET backend

const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5000';

async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      // TODO: attach Bearer token from MSAL
      ...options?.headers,
    },
  });

  if (!res.ok) throw new Error(`API ${res.status}: ${await res.text()}`);
  return res.json() as Promise<T>;
}

// ── Boards ────────────────────────────────────────────────────────────────────
import type { Board, AgentTask, AgentRole, Artifact, Approval } from './types';

export const api = {
  boards: {
    list: () => fetchApi<Board[]>('/api/boards'),
    create: (name: string, description?: string) =>
      fetchApi<Board>('/api/boards', { method: 'POST', body: JSON.stringify({ name, description }) }),
  },

  tasks: {
    list: (boardId: string) => fetchApi<AgentTask[]>(`/api/boards/${boardId}/tasks`),
    get: (boardId: string, taskId: string) => fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}`),
    create: (boardId: string, task: Partial<AgentTask>) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks`, { method: 'POST', body: JSON.stringify(task) }),
    updateStatus: (boardId: string, taskId: string, status: AgentTask['status']) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}/status`, { method: 'PATCH', body: JSON.stringify({ status }) }),
    updatePosition: (boardId: string, taskId: string, x: number, y: number) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}/canvas-position`, { method: 'PATCH', body: JSON.stringify({ x, y }) }),
    connect: (boardId: string, upstreamId: string, downstreamId: string) =>
      fetchApi(`/api/boards/${boardId}/tasks/${upstreamId}/connect`, { method: 'POST', body: JSON.stringify({ downstreamTaskId: downstreamId }) }),
  },

  agentRoles: {
    list: () => fetchApi<AgentRole[]>('/api/agentroles'),
  },

  artifacts: {
    versions: (taskId: string) => fetchApi<Artifact[]>(`/api/artifacts/${taskId}`),
  },

  approvals: {
    pending: () => fetchApi<Approval[]>('/api/approvals/pending'),
    respond: (approvalId: string, runId: string, approved: boolean, notes?: string) =>
      fetchApi<Approval>(`/api/approvals/${approvalId}/respond`, {
        method: 'POST',
        body: JSON.stringify({ runId, approved, notes }),
      }),
  },

  // SSE stream for live run updates
  streamRun: (runId: string, onEvent: (event: import('./types').AgentEvent) => void): (() => void) => {
    const es = new EventSource(`${API_BASE}/api/runs/${runId}/stream`);
    es.onmessage = (e) => {
      try { onEvent(JSON.parse(e.data)); } catch { /* skip malformed */ }
    };
    return () => es.close();
  },

  // CLI Bridge WebSocket
  connectCli: (taskId: string, runId: string, onMessage: (msg: string) => void): WebSocket => {
    const ws = new WebSocket(`${API_BASE.replace('http', 'ws')}/api/tasks/${taskId}/cli/stream?runId=${runId}`);
    ws.onmessage = (e) => onMessage(e.data);
    return ws;
  },
};
