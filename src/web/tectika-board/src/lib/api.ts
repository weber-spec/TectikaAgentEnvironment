// API client — all calls to the .NET backend.

import type {
  Board, AgentTask, AgentRole, Artifact, Approval, WorkflowRun, AgentEvent, TaskEdge, EdgeKind,
} from './types';

// Strip any trailing slash so `${API_BASE}${path}` (paths start with /api) never doubles up.
const API_BASE = (process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5138').replace(/\/+$/, '');

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      // TODO: attach Bearer token from MSAL once auth is wired in production.
      ...options?.headers,
    },
  });

  if (!res.ok) throw new ApiError(res.status, `API ${res.status}: ${await res.text()}`);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Fields the backend's PUT /tasks/{id} accepts. */
export type TaskPatch = Partial<
  Pick<AgentTask, 'title' | 'description' | 'status' | 'priority' | 'assignee' |
    'dueAt' | 'humanAuditorId' | 'canvasPosition'>
>;

export const api = {
  base: API_BASE,

  boards: {
    list: () => fetchApi<Board[]>('/api/boards'),
    get: (boardId: string) => fetchApi<Board>(`/api/boards/${boardId}`),
    create: (name: string, description?: string) =>
      fetchApi<Board>('/api/boards', { method: 'POST', body: JSON.stringify({ name, description }) }),
  },

  tasks: {
    list: (boardId: string) => fetchApi<AgentTask[]>(`/api/boards/${boardId}/tasks`),
    get: (boardId: string, taskId: string) => fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}`),
    create: (boardId: string, task: Partial<AgentTask>) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks`, { method: 'POST', body: JSON.stringify(task) }),
    update: (boardId: string, taskId: string, patch: TaskPatch) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}`, { method: 'PUT', body: JSON.stringify(patch) }),
    remove: (boardId: string, taskId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/tasks/${taskId}`, { method: 'DELETE' }),
    updateStatus: (boardId: string, taskId: string, status: AgentTask['status']) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}/status`, { method: 'PATCH', body: JSON.stringify({ status }) }),
    updatePosition: (boardId: string, taskId: string, x: number, y: number) =>
      fetchApi<AgentTask>(`/api/boards/${boardId}/tasks/${taskId}/canvas-position`, { method: 'PATCH', body: JSON.stringify({ x, y }) }),
  },

  edges: {
    list: (boardId: string) => fetchApi<TaskEdge[]>(`/api/boards/${boardId}/edges`),
    create: (boardId: string, body: { sourceTaskId: string; targetTaskId: string; kind?: EdgeKind; label?: string }) =>
      fetchApi<TaskEdge>(`/api/boards/${boardId}/edges`, { method: 'POST', body: JSON.stringify(body) }),
    update: (boardId: string, edgeId: string, patch: Partial<Pick<TaskEdge, 'kind' | 'label' | 'condition' | 'maxIterations'>>) =>
      fetchApi<TaskEdge>(`/api/boards/${boardId}/edges/${encodeURIComponent(edgeId)}`, { method: 'PUT', body: JSON.stringify(patch) }),
    remove: (boardId: string, edgeId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/edges/${encodeURIComponent(edgeId)}`, { method: 'DELETE' }),
  },

  agentRoles: {
    list: () => fetchApi<AgentRole[]>('/api/agentroles'),
    get: (roleId: string) => fetchApi<AgentRole>(`/api/agentroles/${roleId}`),
    upsert: (role: AgentRole) =>
      fetchApi<AgentRole>('/api/agentroles', { method: 'POST', body: JSON.stringify(role) }),
  },

  artifacts: {
    versions: (taskId: string) => fetchApi<Artifact[]>(`/api/artifacts/${taskId}`),
    save: (taskId: string, content: string, contentType: Artifact['contentType'], runId?: string) =>
      fetchApi<Artifact>('/api/artifacts', {
        method: 'POST',
        body: JSON.stringify({ taskId, content, contentType, runId }),
      }),
  },

  runs: {
    get: (taskId: string, runId: string) => fetchApi<WorkflowRun>(`/api/runs/${taskId}/${runId}`),
  },

  approvals: {
    pending: () => fetchApi<Approval[]>('/api/approvals/pending'),
    respond: (approvalId: string, runId: string, approved: boolean, notes?: string) =>
      fetchApi<Approval>(`/api/approvals/${approvalId}/respond`, {
        method: 'POST',
        body: JSON.stringify({ runId, approved, notes }),
      }),
  },

  // External CLI bridge status (is a local agent currently linked to this task?).
  cliStatus: (taskId: string) => fetchApi<{ taskId: string; connected: boolean }>(`/api/tasks/${taskId}/cli/status`),

  // SSE stream for live run updates.
  streamRun: (runId: string, onEvent: (event: AgentEvent) => void): (() => void) => {
    const es = new EventSource(`${API_BASE}/api/runs/${runId}/stream`);
    es.onmessage = (e) => {
      try { onEvent(JSON.parse(e.data)); } catch { /* skip malformed */ }
    };
    return () => es.close();
  },

  // CLI Bridge WebSocket.
  connectCli: (taskId: string, runId: string, onMessage: (msg: string) => void): WebSocket => {
    const ws = new WebSocket(`${API_BASE.replace('http', 'ws')}/api/tasks/${taskId}/cli/stream?runId=${runId}`);
    ws.onmessage = (e) => onMessage(e.data);
    return ws;
  },
};
