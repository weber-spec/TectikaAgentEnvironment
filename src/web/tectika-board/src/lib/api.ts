// API client — all calls to the .NET backend.

import type {
  Board, AgentTask, AgentRole, AgentUpsertResult, Artifact, Approval, WorkflowRun, AgentEvent, HumanInteraction, TaskEdge, EdgeKind, RunEvent,
  RepoMeta, BranchInfo, TreeEntry, FileContent, CommitInfo, PullRequestInfo, CompareResult,
} from './types';
import { trackEvent, trackException, redact } from './telemetry';

// Strip any trailing slash so `${API_BASE}${path}` (paths start with /api) never doubles up.
const API_BASE = (process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5138').replace(/\/+$/, '');

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const method = options?.method ?? 'GET';
  trackEvent('[ApiRequest]', { method, path, body: redact(options?.body as string | undefined) });
  try {
    const res = await fetch(`${API_BASE}${path}`, {
      ...options,
      headers: {
        'Content-Type': 'application/json',
        // TODO: attach Bearer token from MSAL once auth is wired in production.
        ...options?.headers,
      },
    });

    if (!res.ok) {
      const text = await res.text();
      trackEvent('[ApiError]', { method, path, status: res.status, body: redact(text) });
      throw new ApiError(res.status, `API ${res.status}: ${text}`);
    }
    if (res.status === 204) return undefined as T;
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  } catch (err) {
    if (!(err instanceof ApiError)) trackException(err, { method, path });
    throw err;
  }
}

/** Fields the backend's PUT /tasks/{id} accepts. */
export type TaskPatch = Partial<
  Pick<AgentTask, 'title' | 'description' | 'status' | 'priority' | 'assignee' |
    'dueAt' | 'humanAuditorId' | 'canvasPosition' | 'prompt'>
>;

export const api = {
  base: API_BASE,

  boards: {
    list: () => fetchApi<Board[]>('/api/boards'),
    get: (boardId: string) => fetchApi<Board>(`/api/boards/${boardId}`),
    create: (name: string, description?: string) =>
      fetchApi<Board>('/api/boards', { method: 'POST', body: JSON.stringify({ name, description }) }),
    update: (boardId: string, name: string, description?: string) =>
      fetchApi<Board>(`/api/boards/${boardId}`, { method: 'PUT', body: JSON.stringify({ name, description }) }),
    remove: (boardId: string) =>
      fetchApi<void>(`/api/boards/${boardId}`, { method: 'DELETE' }),
    connectGitHub: (boardId: string, repoUrl: string, pat: string) =>
      fetchApi<Board>(`/api/boards/${boardId}/github`, {
        method: 'PUT', body: JSON.stringify({ repoUrl, pat }),
      }),
    disconnectGitHub: (boardId: string) =>
      fetchApi<Board>(`/api/boards/${boardId}/github`, { method: 'DELETE' }),
  },

  repo: {
    meta: (boardId: string) => fetchApi<RepoMeta>(`/api/boards/${boardId}/repo/meta`),
    branches: (boardId: string) => fetchApi<BranchInfo[]>(`/api/boards/${boardId}/repo/branches`),
    tree: (boardId: string, ref?: string, path?: string) =>
      fetchApi<TreeEntry[]>(`/api/boards/${boardId}/repo/tree?ref=${encodeURIComponent(ref ?? '')}&path=${encodeURIComponent(path ?? '')}`),
    file: (boardId: string, path: string, ref?: string) =>
      fetchApi<FileContent>(`/api/boards/${boardId}/repo/file?ref=${encodeURIComponent(ref ?? '')}&path=${encodeURIComponent(path)}`),
    commits: (boardId: string, ref?: string, path?: string, page = 1) =>
      fetchApi<CommitInfo[]>(`/api/boards/${boardId}/repo/commits?ref=${encodeURIComponent(ref ?? '')}&path=${encodeURIComponent(path ?? '')}&page=${page}`),
    pulls: (boardId: string, state = 'open') =>
      fetchApi<PullRequestInfo[]>(`/api/boards/${boardId}/repo/pulls?state=${encodeURIComponent(state)}`),
    compare: (boardId: string, base: string, head: string) =>
      fetchApi<CompareResult>(`/api/boards/${boardId}/repo/compare?base=${encodeURIComponent(base)}&head=${encodeURIComponent(head)}`),
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
    /** Chat with a task: starts a steerable run seeded with the message, or injects it into the live run. */
    chat: (boardId: string, taskId: string, text: string) =>
      fetchApi<{ runId: string; injected: boolean }>(`/api/boards/${boardId}/tasks/${taskId}/chat`, { method: 'POST', body: JSON.stringify({ text }) }),
    /** Persisted run trace (Activity tab + chat transcript), oldest-first. */
    events: (boardId: string, taskId: string, sinceRound?: number) =>
      fetchApi<RunEvent[]>(`/api/boards/${boardId}/tasks/${taskId}/events${sinceRound != null ? `?sinceRound=${sinceRound}` : ''}`),
    /** Slash-command effects. */
    clear: (boardId: string, taskId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/tasks/${taskId}/clear`, { method: 'POST' }),
    stop: (boardId: string, taskId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/tasks/${taskId}/stop`, { method: 'POST' }),
    compact: (boardId: string, taskId: string) =>
      fetchApi<{ summarized: boolean }>(`/api/boards/${boardId}/tasks/${taskId}/compact`, { method: 'POST' }),
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
    /** Upserts an agent role. Returns the saved AgentRole directly (unwraps the wrapper). */
    upsert: async (role: AgentRole): Promise<AgentRole> => {
      const result = await fetchApi<AgentUpsertResult>('/api/agentroles', { method: 'POST', body: JSON.stringify(role) });
      return result.role;
    },
    /** Upserts an agent role and returns the full {role, synced, error} response. */
    upsertFull: (role: AgentRole): Promise<AgentUpsertResult> =>
      fetchApi<AgentUpsertResult>('/api/agentroles', { method: 'POST', body: JSON.stringify(role) }),
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
    start: (boardId: string, taskId: string) =>
      fetchApi<{ runId: string; taskId: string; status: string; streamUrl: string }>(
        '/api/runs/start',
        { method: 'POST', body: JSON.stringify({ boardId, taskId, pipeline: null }) }
      ),
  },

  approvals: {
    pending: () => fetchApi<Approval[]>('/api/approvals/pending'),
    respond: (approvalId: string, runId: string, approved: boolean, notes?: string) =>
      fetchApi<Approval>(`/api/approvals/${approvalId}/respond`, {
        method: 'POST',
        body: JSON.stringify({ runId, approved, notes }),
      }),
  },

  interactions: {
    pending: () => fetchApi<HumanInteraction[]>('/api/interactions/pending'),
    respond: (
      interactionId: string,
      runId: string,
      opts: { selectedIndex?: number; answer?: string; approved?: boolean; notes?: string }
    ) =>
      fetchApi<HumanInteraction>(`/api/interactions/${interactionId}/respond`, {
        method: 'POST',
        body: JSON.stringify({ runId, ...opts }),
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
