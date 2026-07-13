// API client — all calls to the .NET backend.

import type {
  Board, AgentTask, AgentRole, AgentUpsertResult, Artifact, WorkflowRun, AgentEvent, HumanInteraction, TaskEdge, EdgeKind, RunEvent,
  RepoMeta, BranchInfo, TreeEntry, FileContent, CommitInfo, PullRequestInfo, CompareResult,
  UsageRollup, UsageEventsPage, PricingCatalog, UsageTimePoint, AgentUsage,
  PreviewSession, BoardWorkspaceStatusDto, ResetBoardResult,
  Comment, CommentKind, NoteType,
  Channel, ChannelMessage,
  ResendDomain,
  Connection, ConnectionCatalogEntry, CreateConnectionInput, BoardConnectionBinding, ToolsCatalog,
} from './types';
import { trackEvent, trackException, redact } from './telemetry';

/** QA §4.5 — consolidated board snapshot returned by GET /api/boards/{id}/state, so the live poll is a
 * single round-trip instead of tasks + edges + N usage + N run requests every tick. */
export interface BoardState {
  tasks: AgentTask[];
  edges: TaskEdge[];
  usageByTaskId: Record<string, UsageRollup>;
  runsById: Record<string, WorkflowRun>;
}

// Strip any trailing slash so `${API_BASE}${path}` (paths start with /api) never doubles up.
const API_BASE = (process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5138').replace(/\/+$/, '');

export class ApiError extends Error {
  readonly status: number;
  /** Parsed JSON error body when the server returned one (e.g. a 409 { error, … } payload); undefined otherwise. */
  readonly body: unknown;
  // Plain field assignment (not a TS parameter property) so the file is importable under
  // Node's `--experimental-strip-types` test runner, which can't transform param properties.
  constructor(status: number, message: string, body?: unknown) {
    super(message);
    this.status = status;
    this.body = body;
    this.name = 'ApiError';
  }
}

/** A request that never came back. Distinct from ApiError's HTTP failures, and status 0 on purpose:
 *  callers that branch on a status (api.preview.get treats 404 as "no preview") must not mistake a
 *  timeout for a real answer from the server. */
export class ApiTimeoutError extends ApiError {
  constructor(method: string, path: string, ms: number) {
    super(0,
      `API timeout after ${ms}ms: ${method} ${path}. If several of these fire at once, the browser's ` +
      `per-origin connection pool is likely exhausted by open EventSource streams.`);
    this.name = 'ApiTimeoutError';
  }
}

/** Most calls are quick. Slow ones (container provisioning, LLM summarisation) pass their own timeoutMs —
 *  a blanket cap here would fail them falsely while the work carried on server-side. */
const DEFAULT_TIMEOUT_MS = 25_000;
/** Bulk data work: board reset/clone, LLM summarisation. */
const SLOW_TIMEOUT_MS = 60_000;
/** Container provisioning (ACI create + ACR image pull), which the request waits on synchronously. */
const PROVISION_TIMEOUT_MS = 120_000;

function withTimeout(signal: AbortSignal | null | undefined, timeout: AbortSignal): AbortSignal {
  if (!signal) return timeout;
  if (typeof AbortSignal.any === 'function') return AbortSignal.any([signal, timeout]);
  const ctrl = new AbortController();
  for (const s of [signal, timeout]) {
    if (s.aborted) { ctrl.abort(s.reason); break; }
    s.addEventListener('abort', () => ctrl.abort(s.reason), { once: true });
  }
  return ctrl.signal;
}

type ApiRequestInit = RequestInit & { timeoutMs?: number };

async function fetchApi<T>(path: string, options?: ApiRequestInit): Promise<T> {
  const method = options?.method ?? 'GET';
  const timeoutMs = options?.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  trackEvent('[ApiRequest]', { method, path, body: redact(options?.body as string | undefined) });
  // Without this a stalled request hangs forever rather than failing: that's what turned a saturated
  // connection pool into "the board sits on Loading… and Reset & run does nothing, with no error".
  const timeout = AbortSignal.timeout(timeoutMs);
  try {
    const res = await fetch(`${API_BASE}${path}`, {
      ...options,
      signal: withTimeout(options?.signal, timeout),
      headers: {
        'Content-Type': 'application/json',
        // TODO: attach Bearer token from MSAL once auth is wired in production.
        ...options?.headers,
      },
    });

    if (!res.ok) {
      const text = await res.text();
      trackEvent('[ApiError]', { method, path, status: res.status, body: redact(text) });
      let parsed: unknown;
      try { parsed = text ? JSON.parse(text) : undefined; } catch { /* non-JSON body */ }
      throw new ApiError(res.status, `API ${res.status}: ${text}`, parsed);
    }
    if (res.status === 204) return undefined as T;
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  } catch (err) {
    // Our timeout, not the caller's own abort — surface it as a real, named failure.
    if (timeout.aborted && !options?.signal?.aborted) {
      const e = new ApiTimeoutError(method, path, timeoutMs);
      trackException(e, { method, path });
      throw e;
    }
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
    /** QA §4.5 — one consolidated snapshot (tasks + edges + per-task usage + active runs) for the live poll. */
    state: (boardId: string) => fetchApi<BoardState>(`/api/boards/${boardId}/state`),
    create: (name: string, description?: string) =>
      fetchApi<Board>('/api/boards', { method: 'POST', body: JSON.stringify({ name, description }) }),
    update: (boardId: string, name: string, description?: string) =>
      fetchApi<Board>(`/api/boards/${boardId}`, { method: 'PUT', body: JSON.stringify({ name, description }) }),
    remove: (boardId: string) =>
      fetchApi<void>(`/api/boards/${boardId}`, { method: 'DELETE' }),
    connectGitHub: (boardId: string, connectionId: string, repoUrl: string) =>
      fetchApi<Board>(`/api/boards/${boardId}/github`, {
        method: 'PUT', body: JSON.stringify({ connectionId, repoUrl }),
      }),
    disconnectGitHub: (boardId: string) =>
      fetchApi<Board>(`/api/boards/${boardId}/github`, { method: 'DELETE' }),
    // Purges/copies every document on the board — well past the default timeout on a busy board.
    reset: (boardId: string, clearRepo: boolean) =>
      fetchApi<ResetBoardResult>(`/api/boards/${boardId}/reset`, {
        method: 'POST', body: JSON.stringify({ clearRepo }), timeoutMs: SLOW_TIMEOUT_MS,
      }),
    clone: (boardId: string, opts: { name?: string; includeData: boolean }) =>
      fetchApi<Board>(`/api/boards/${boardId}/clone`, {
        method: 'POST', body: JSON.stringify({ name: opts.name, includeData: opts.includeData }),
        timeoutMs: SLOW_TIMEOUT_MS,
      }),
    workspace: {
      get: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace`),
      // Provisioning waits on the container synchronously (WorkspaceControlService.StartAsync → ACI create +
      // image pull), which routinely runs past a minute. Timing out here would report a false failure while
      // the container kept coming up, stranding the board in Provisioning.
      start: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace`, { method: 'POST', timeoutMs: PROVISION_TIMEOUT_MS }),
      restart: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace/restart`, { method: 'POST', timeoutMs: PROVISION_TIMEOUT_MS }),
      terminate: (boardId: string) => fetchApi<BoardWorkspaceStatusDto>(`/api/boards/${boardId}/workspace`, { method: 'DELETE', timeoutMs: SLOW_TIMEOUT_MS }),
    },
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

  preview: {
    /** Start (or re-provision) a live preview for a board's branch. Provisions a container — see workspace.start. */
    start: (boardId: string, branch: string) =>
      fetchApi<PreviewSession>(`/api/boards/${boardId}/preview`, {
        method: 'POST', body: JSON.stringify({ branch }), timeoutMs: PROVISION_TIMEOUT_MS,
      }),
    /** Current preview session, or null when none is active (backend 404s). Polled while Provisioning. */
    get: async (boardId: string): Promise<PreviewSession | null> => {
      try {
        return await fetchApi<PreviewSession>(`/api/boards/${boardId}/preview`);
      } catch (err) {
        if (err instanceof ApiError && err.status === 404) return null;
        throw err;
      }
    },
    /** Keep-alive: pushes back the session's expiry while the tab is open. */
    heartbeat: (boardId: string) =>
      fetchApi<PreviewSession>(`/api/boards/${boardId}/preview/heartbeat`, { method: 'POST' }),
    stop: (boardId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/preview`, { method: 'DELETE' }),
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
    /** Summarises the conversation through the model — an LLM round-trip, not a CRUD call. */
    compact: (boardId: string, taskId: string) =>
      fetchApi<{ summarized: boolean }>(`/api/boards/${boardId}/tasks/${taskId}/compact`, { method: 'POST', timeoutMs: SLOW_TIMEOUT_MS }),
  },

  comments: {
    list: (boardId: string, taskId: string) =>
      fetchApi<{ comments: Comment[]; lastReadAt: string | null }>(`/api/boards/${boardId}/tasks/${taskId}/comments`),
    create: (boardId: string, taskId: string, input: { kind: CommentKind; noteType?: NoteType; body: string; mentions: string[] }) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments`, { method: 'POST', body: JSON.stringify(input) }),
    update: (boardId: string, taskId: string, commentId: string, input: { body: string; noteType?: NoteType }) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}`, { method: 'PUT', body: JSON.stringify(input) }),
    remove: (boardId: string, taskId: string, commentId: string) =>
      fetchApi<{ deleted: boolean }>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}`, { method: 'DELETE' }),
    react: (boardId: string, taskId: string, commentId: string, emoji: string) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}/reactions`, { method: 'POST', body: JSON.stringify({ emoji }) }),
    share: (boardId: string, taskId: string, commentId: string, shared: boolean) =>
      fetchApi<Comment>(`/api/boards/${boardId}/tasks/${taskId}/comments/${commentId}/share`, { method: 'POST', body: JSON.stringify({ shared }) }),
    markRead: (boardId: string, taskId: string) =>
      fetchApi<{ lastReadAt: string }>(`/api/boards/${boardId}/tasks/${taskId}/comments/read`, { method: 'POST' }),
  },

  /** Internal Slack-like channels + DMs. */
  channels: {
    list: () => fetchApi<Channel[]>('/api/channels'),
    get: (channelId: string) => fetchApi<Channel>(`/api/channels/${channelId}`),
    create: (input: { name: string; description?: string; memberIds?: string[] }) =>
      fetchApi<Channel>('/api/channels', { method: 'POST', body: JSON.stringify(input) }),
    addMember: (channelId: string, memberId: string, memberType?: 'human' | 'agent') =>
      fetchApi<Channel>(`/api/channels/${channelId}/members`, { method: 'POST', body: JSON.stringify({ memberId, memberType }) }),
    removeMember: (channelId: string, memberId: string) =>
      fetchApi<Channel>(`/api/channels/${channelId}/members/${encodeURIComponent(memberId)}`, { method: 'DELETE' }),
    /** Get-or-create a DM (deterministic id — never duplicated). */
    dm: (otherMemberId: string, otherMemberType?: 'human' | 'agent') =>
      fetchApi<Channel>('/api/channels/dm', { method: 'POST', body: JSON.stringify({ otherMemberId, otherMemberType }) }),
    messages: (channelId: string, since?: string) =>
      fetchApi<ChannelMessage[]>(`/api/channels/${channelId}/messages${since ? `?since=${encodeURIComponent(since)}` : ''}`),
    postMessage: (channelId: string, body: string, mentions?: string[]) =>
      fetchApi<ChannelMessage>(`/api/channels/${channelId}/messages`, { method: 'POST', body: JSON.stringify({ body, mentions }) }),
    react: (channelId: string, messageId: string, emoji: string) =>
      fetchApi<ChannelMessage>(`/api/channels/${channelId}/messages/${messageId}/reactions`, { method: 'POST', body: JSON.stringify({ emoji }) }),
    markRead: (channelId: string) =>
      fetchApi<{ lastReadAt: string }>(`/api/channels/${channelId}/read`, { method: 'POST' }),
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
    /** Upserts an agent role. Returns the saved AgentRole directly (unwraps the wrapper).
     *  anthropicApiKey (ClaudeCode engine) is sent transiently and stored server-side in Key Vault. */
    upsert: async (role: AgentRole, anthropicApiKey?: string): Promise<AgentRole> => {
      const result = await fetchApi<AgentUpsertResult>('/api/agentroles', { method: 'POST', body: JSON.stringify({ role, anthropicApiKey }) });
      return result.role;
    },
    /** Upserts an agent role and returns the full {role, synced, error} response. */
    upsertFull: (role: AgentRole, anthropicApiKey?: string): Promise<AgentUpsertResult> =>
      fetchApi<AgentUpsertResult>('/api/agentroles', { method: 'POST', body: JSON.stringify({ role, anthropicApiKey }) }),
    /** Deletes an agent role. The backend also removes the underlying Foundry agent (best-effort). */
    remove: (roleId: string) =>
      fetchApi<void>(`/api/agentroles/${roleId}`, { method: 'DELETE' }),
  },

  /** Per-board bindings: which tenant connections a board enabled (+ per-board config, e.g. the GitHub repo). */
  boardConnections: {
    list: (boardId: string) => fetchApi<BoardConnectionBinding[]>(`/api/boards/${boardId}/connections`),
    bind: (boardId: string, connectionId: string, config?: Record<string, string>) =>
      fetchApi<BoardConnectionBinding>(`/api/boards/${boardId}/connections/${connectionId}`,
        { method: 'PUT', body: JSON.stringify({ config: config ?? {} }) }),
    unbind: (boardId: string, connectionId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/connections/${connectionId}`, { method: 'DELETE' }),
  },

  /** Tenant-level connection registry (the Connections page). */
  connections: {
    catalog: () => fetchApi<ConnectionCatalogEntry[]>('/api/connections/catalog'),
    list: () => fetchApi<Connection[]>('/api/connections'),
    create: (input: CreateConnectionInput) =>
      fetchApi<Connection>('/api/connections', { method: 'POST', body: JSON.stringify(input) }),
    validate: (connectionId: string) =>
      fetchApi<Connection>(`/api/connections/${connectionId}/validate`, { method: 'POST' }),
    /** Live Claude model ids for an Anthropic connection (curated fallback for OAuth / on failure). */
    models: (connectionId: string) =>
      fetchApi<string[]>(`/api/connections/${connectionId}/models`),
    remove: (connectionId: string) =>
      fetchApi<void>(`/api/connections/${connectionId}`, { method: 'DELETE' }),
  },

  /** Unified capability catalog + tenant-level global tool enable/disable (the Tools page). */
  tools: {
    catalog: () => fetchApi<ToolsCatalog>('/api/tools/catalog'),
    setEnabled: (toolId: string, enabled: boolean) =>
      fetchApi<{ toolId: string; enabled: boolean }>(`/api/tools/${encodeURIComponent(toolId)}/enabled`,
        { method: 'PUT', body: JSON.stringify({ enabled }) }),
  },

  email: {
    domains: (boardId: string) =>
      fetchApi<ResendDomain[]>(`/api/boards/${boardId}/email/domains`),
    addDomain: (boardId: string, name: string) =>
      fetchApi<ResendDomain>(`/api/boards/${boardId}/email/domains`, { method: 'POST', body: JSON.stringify({ name }) }),
    getDomain: (boardId: string, id: string) =>
      fetchApi<ResendDomain>(`/api/boards/${boardId}/email/domains/${id}`),
    verifyDomain: (boardId: string, id: string) =>
      fetchApi<ResendDomain>(`/api/boards/${boardId}/email/domains/${id}/verify`, { method: 'POST' }),
    deleteDomain: (boardId: string, id: string) =>
      fetchApi<void>(`/api/boards/${boardId}/email/domains/${id}`, { method: 'DELETE' }),
    setFrom: (boardId: string, from: string) =>
      fetchApi<{ defaultFrom: string }>(`/api/boards/${boardId}/email/from`, { method: 'PUT', body: JSON.stringify({ from }) }),
  },

  models: {
    /** Available model names for the agent picker (Foundry deployments, or the mock list). */
    list: () => fetchApi<string[]>('/api/models'),
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

  // Run events are NOT streamed per-run from the browser: see lib/board-stream.ts, which multiplexes every
  // run on a board onto one connection. A stream per run blew past the browser's ~6-connections-per-origin
  // cap and left every other request hanging. The server still serves /api/runs/{id}/stream for the CLI.

  // SSE stream for live channel messages. One per open channel, on its own page — not a fan-out.
  streamChannel: (channelId: string, onMessage: (message: ChannelMessage) => void): (() => void) => {
    const es = new EventSource(`${API_BASE}/api/channels/${channelId}/stream`);
    es.onmessage = (e) => {
      try { onMessage(JSON.parse(e.data)); } catch { /* skip malformed */ }
    };
    return () => es.close();
  },

  usage: {
    project: () => fetchApi<UsageRollup>('/api/usage/project'),
    board: (boardId: string) => fetchApi<UsageRollup>(`/api/usage/board/${boardId}`),
    task: (taskId: string) => fetchApi<UsageRollup>(`/api/usage/task/${taskId}`),
    events: (taskId: string, max = 50) => fetchApi<UsageEventsPage>(`/api/usage/task/${taskId}/events?max=${max}`),
    projectTimeseries: (days = 14) => fetchApi<UsageTimePoint[]>(`/api/usage/project/timeseries?days=${days}`),
    boardTimeseries: (boardId: string, days = 14) => fetchApi<UsageTimePoint[]>(`/api/usage/board/${boardId}/timeseries?days=${days}`),
    byAgentProject: (days = 14) => fetchApi<AgentUsage[]>(`/api/usage/project/by-agent?days=${days}`),
    byAgentBoard: (boardId: string, days = 14) => fetchApi<AgentUsage[]>(`/api/usage/board/${boardId}/by-agent?days=${days}`),
    pricing: () => fetchApi<PricingCatalog>('/api/usage/pricing'),
  },
};
