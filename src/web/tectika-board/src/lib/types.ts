// ─────────────────────────────────────────────────────────────────────────────
// Mirror of C# models — keep in sync with TectikaAgents.Core.Models.
// (Enums are serialized as their string names by the API; see JsonStringEnumConverter.)
// ─────────────────────────────────────────────────────────────────────────────

export type AgentTaskStatus = 'Backlog' | 'InProgress' | 'AwaitingInteraction' | 'Blocked' | 'Review' | 'Done' | 'Failed';
export type TaskPriority = 'Critical' | 'High' | 'Medium' | 'Low';
export type AssigneeType = 'Agent' | 'Human';
export type ArtifactContentType = 'Code' | 'Markdown' | 'Json' | 'Data';
export type ArtifactOrigin = 'Agent' | 'HumanEdit' | 'CliBridge';

export type OutputKind = 'Document' | 'Code' | 'Design' | 'Dataset' | 'Deployment' | 'Link';

export interface InlineContent {
  contentType: ArtifactContentType;
  content: string;
}

export interface ExternalRef {
  provider: string;
  locator: Record<string, unknown>;
  previewUrl?: string;
}

export type FileLinkSource = 'Workspace' | 'Repo';

export interface FileLink {
  path: string;
  source: FileLinkSource;
  previewUrl?: string;
}

export interface Output {
  id: string;
  kind: OutputKind;
  label?: string;
  inline?: InlineContent;
  external?: ExternalRef;
  links?: FileLink[];
}

export type RunStatus = 'Pending' | 'Running' | 'AwaitingInteraction' | 'NeedsRevision' | 'Completed' | 'Failed' | 'Cancelled';
export type TriggerSource = 'Manual' | 'Supervisor' | 'WebhookGitHub' | 'WebhookJira' | 'Schedule' | 'CliBridge';
export type InteractionType = 'Approval' | 'Selection' | 'Question';
export type InteractionStatus = 'Pending' | 'Responded' | 'Expired';

export type BoardRunPhase =
  | { kind: 'idle' }
  | { kind: 'running'; taskIds: string[] }
  | { kind: 'done'; status: 'AwaitingInteraction' | 'Failed' | 'Completed' };

export interface GitHubRepoConnection {
  repoUrl: string;
  owner: string;
  repo: string;
  patSecretName: string;
}

export interface GitHubPermissions {
  canRead: boolean;
}


export interface ResendDnsRecord { record?: string; name: string; type: string; ttl?: string; status?: string; value: string; priority?: number; }
export interface ResendDomain { id: string; name: string; status: string; records?: ResendDnsRecord[]; }

/** GET /api/mcp/catalog item — UI projection (no endpoint/auth internals). */
export interface McpCatalogEntry {
  id: string;
  displayName: string;
  description: string;
  tokenHint: string;
  helpUrl?: string | null;
  readToolCount: number;
  writeToolCount: number;
}

// ── Connections (tenant-level registry) ──────────────────────────────────────

export type ConnectionStatus = 'Connected' | 'Error' | 'Disconnected';
export type ConnectionScope = 'Organization' | 'Private';
/** Catalog category buckets (mirror C# ConnectionCategory). */
export type ConnectionCategory = 'model' | 'agent-tool' | 'source-control';

/** One credential input a catalog entry requires (mirrors C# AuthField). */
export interface ConnectionAuthField {
  name: string;
  label: string;
  type: string;   // e.g. 'password' | 'text'
  hint: string;
  secret: boolean;
}

/** GET /api/connections/catalog item — the "Available" gallery projection. */
export interface ConnectionCatalogEntry {
  id: string;
  displayName: string;
  description: string;
  category: ConnectionCategory;
  iconKey: string;
  helpUrl?: string | null;
  supportsMultiple: boolean;
  authFields: ConnectionAuthField[];
  readToolCount: number;
  writeToolCount: number;
}

/** A tenant-level connection instance (mirrors C# Connection). */
export interface Connection {
  id: string;
  tenantId: string;
  catalogId: string;
  category: ConnectionCategory | string;
  displayName: string;
  secretName: string;
  status: ConnectionStatus;
  lastValidatedAt?: string | null;
  createdBy?: string | null;
  createdAt: string;
  metadata: Record<string, string>;
  isSystem: boolean;
  scope: ConnectionScope;
}

// ── Tools catalog (the Tools page: capabilities + global enable/disable) ──────
export type ToolSource = 'board' | 'foundry' | 'integration';
export interface ToolItem {
  toolId: string;
  name: string;
  description: string;
  source: ToolSource;
  group: string;
  enabled: boolean;
  lockable: boolean;      // false = core tool, always on (no toggle)
  needsSetup: boolean;    // board: needs workspace/GitHub · foundry: needs a project connection · integration: no connection yet
  iconKey?: string | null;
  isWrite?: boolean | null;
  connectionCatalogId?: string | null;
}
export interface ToolsCatalog {
  board: ToolItem[];
  foundry: ToolItem[];
  integration: ToolItem[];
}

/** POST /api/connections body. */
export interface CreateConnectionInput {
  catalogId: string;
  displayName?: string;
  scope?: ConnectionScope;
  secrets?: Record<string, string>;
  metadata?: Record<string, string>;
}

export interface Board {
  id: string;
  tenantId: string;
  name: string;
  description: string;
  ownerId: string;
  columns: string[];
  createdAt: string;
  github?: GitHubRepoConnection | null;
  /** Tenant connections enabled on this board (+ per-board binding config, e.g. the GitHub repo). */
  connections?: BoardConnectionBinding[];
  workspaceContainerName?: string | null;
  workspaceEndpoint?: string | null;
  workspaceStatus?: 'None' | 'Provisioning' | 'Ready';
  workspaceLastUsedAt?: string | null;
}

/** Binds a tenant connection to a board (enables it) + per-board config (mirrors C# BoardConnectionBinding). */
export interface BoardConnectionBinding {
  connectionId: string;
  config: Record<string, string>;
}

export type WorkspaceAzureState = 'NotFound' | 'Provisioning' | 'Running' | 'Stopped' | 'Failed' | 'Unknown';

export interface BoardWorkspaceStatusDto {
  status: 'None' | 'Provisioning' | 'Ready';
  azureState: WorkspaceAzureState;
  containerName?: string | null;
  endpoint?: string | null;
  lastUsedAt?: string | null;
  idleShutdownAt?: string | null;
  hasActiveRuns: boolean;
  image: string;
}

export interface ResetBoardResult {
  tasksReset: number;
  runsCancelled: number;
  workspaceTerminated: boolean;
  repoDisconnected: boolean;
}

export interface TaskAssignee {
  type: AssigneeType;
  id: string;
}

export interface CanvasPosition {
  x: number;
  y: number;
}

export interface AgentTask {
  id: string;
  tenantId: string;
  boardId: string;
  title: string;
  description: string;
  status: AgentTaskStatus;
  priority: TaskPriority;
  assignee: TaskAssignee;
  createdBy: string;
  dependencies: string[];
  workflowRunId?: string;
  triggerSource?: TriggerSource;
  triggerMeta?: Record<string, string>;
  currentArtifactId?: string;
  canvasPosition?: CanvasPosition;
  humanAuditorId?: string;
  taskBrief?: string;
  artifactSummary?: string;
  foundryThreadId?: string;
  prompt?: string;
  chatClearedAt?: string;
  createdAt: string;
  dueAt?: string;
}

// ── Steerable run trace (mirrors backend RunEvent; Activity tab + chat transcript) ──
export type RunEventKind =
  | 'Thinking' | 'RoundStarted' | 'ToolCall' | 'ToolResult' | 'ArtifactWritten'
  | 'UserMessage' | 'AgentMessage' | 'InteractionRequired' | 'ApprovalRequired'
  | 'RevisionRequested' | 'RoundCompleted' | 'RunCompleted' | 'RunFailed';

export interface RunEvent {
  id: string;
  taskId: string;
  runId: string;
  round: number;
  parentId?: string | null;
  kind: RunEventKind;
  title?: string;
  detail?: string;
  toolName?: string;
  toolArgsSummary?: string;
  resultSummary?: string;
  tokenUsage?: TokenUsage;
  timestamp: string;
}

// ── Typed edges — server-persisted source of truth for the task graph ──────────
export type EdgeKind = 'Dependency' | 'QaFeedback';

export interface TaskEdge {
  id: string;            // "{sourceTaskId}->{targetTaskId}"
  tenantId: string;
  boardId: string;
  sourceTaskId: string;
  targetTaskId: string;
  kind: EdgeKind;
  label?: string;
  condition?: string;
  maxIterations: number;
  currentIterations: number;
  createdAt: string;
  updatedAt: string;
}

export interface AgentPermissions {
  canUseWorkspace: boolean;
  canPushCode: boolean;
  canDeploy: boolean;
  requiresOboFor: string[];
  requiresApprovalFor: string[];
}

export interface AgentRole {
  id: string;
  tenantId: string;
  displayName: string;
  systemPrompt: string;
  executionEngine?: ExecutionEngine;
  /** How a ClaudeCode role authenticates: pay-as-you-go API key, or a Pro/Max subscription OAuth token. */
  claudeAuth?: ClaudeAuthMode;
  /** Key Vault secret name for the Claude credential (ClaudeCode engine). The value is never returned. */
  apiKeySecretName?: string | null;
  foundryAgentId?: string | null;
  foundryAgentHash?: string | null;
  tools: string[];
  /** Tenant connections (agent-tool category) this role may use; catalogId is denormalized on each ref. */
  connections: AgentConnectionRef[];
  /** The tenant "model" connection powering this role (Foundry system / Anthropic). Forward-looking. */
  modelConnectionId?: string | null;
  /** Foundry built-in tool ids this role enables (Foundry engine only), e.g. ["code_interpreter"]. */
  foundryTools?: string[];
  permissions: AgentPermissions;
  escalateTo?: string;
  modelOverride?: string;
  githubPermissions?: GitHubPermissions | null;
  createdAt?: string;
  updatedAt?: string;
}

/** An agent's reference to a tenant connection + write opt-in (mirrors C# AgentConnectionRef). */
export interface AgentConnectionRef {
  connectionId: string;
  catalogId: string;
  writeEnabled: boolean;
}

export type ExecutionEngine = 'Foundry' | 'ClaudeCode';
export type ClaudeAuthMode = 'ApiKey' | 'OAuthToken';

/** Response shape from POST /api/agentroles (upsert). */
export interface AgentUpsertResult {
  role: AgentRole;
  synced: boolean;
  error?: string | null;
}

/** A turn in the interactive agent workspace conversation. */
export interface ChatTurn {
  id: string;
  author: 'human' | 'agent' | 'tool';
  text: string;
  createdAt: string;
  toolName?: string;
}

export interface TokenUsage {
  input: number;
  cachedInput: number;
  output: number;
  reasoning: number;
  total: number;
}

export interface UsageBucket {
  tokens: TokenUsage;
  costUsd: number;
  eventCount: number;
}

export interface SessionBucket extends UsageBucket {
  sessionId: string;
  since: string;
  /** Per-model breakdown for the current session only, keyed by "provider/model". */
  perModel: Record<string, UsageBucket>;
}

export type UsageScope = 'Project' | 'Board' | 'Task';

export interface UsageRollup {
  id: string;
  tenantId: string;
  scope: UsageScope;
  scopeId: string;
  lifetime: UsageBucket;
  perModel: Record<string, UsageBucket>;
  currentSession?: SessionBucket | null;
  updatedAt: string;
}

export interface UsageEvent {
  id: string;
  taskId: string;
  runId: string;
  step: number;
  round: number;
  agentRoleName: string;
  provider: string;
  model: string;
  usage: TokenUsage;
  costUsd: number;
  pricingMissing: boolean;
  currency: string;
  timestamp: string;
}

export interface UsageEventsPage {
  items: UsageEvent[];
  continuationToken?: string | null;
}

export interface ModelDayBucket {
  tokens: number;
  costUsd: number;
}

export interface UsageTimePoint {
  date: string;     // "yyyy-MM-dd"
  tokens: number;
  costUsd: number;
  input: number;
  cachedInput: number;
  output: number;
  reasoning: number;
  perModel: Record<string, ModelDayBucket>;  // keyed by "provider/model"
}

/** Ledger-truth usage aggregated per agent role. */
export interface AgentUsage {
  agentRoleId: string;
  agentRoleName: string;
  tokens: TokenUsage;
  costUsd: number;
  eventCount: number;
}

export interface ModelPrice {
  provider: string;
  model: string;
  modelVersion?: string;
  inputPerMillion: number;
  cachedInputPerMillion: number;
  outputPerMillion: number;
  currency: string;
  effectiveFrom: string;
}

export interface PricingCatalog {
  version: string;
  prices: ModelPrice[];
}

export interface WorkflowRun {
  id: string;
  tenantId: string;
  taskId: string;
  currentStep: number;
  status: RunStatus;
  durableFunctionInstanceId?: string;
  totalTokens: number;
  estimatedCostUsd: number;
  startedAt: string;
  completedAt?: string;
}

export interface UpstreamArtifactRef {
  taskId: string;
  artifactId: string;
  version: number;
  contentType: ArtifactContentType;
}

export interface ArtifactInputContext {
  upstreamArtifacts: UpstreamArtifactRef[];
  humanContext?: string;
}

export interface Artifact {
  id: string;
  tenantId: string;
  taskId: string;
  runId?: string;
  version: number;
  contentType: ArtifactContentType;
  content: string;
  inputContext: ArtifactInputContext;
  internalLogs: string[];
  origin: ArtifactOrigin;
  summary?: string;
  outputs: Output[];
  createdAt: string;
  updatedAt: string;
}

export interface AgentEvent {
  type: string;
  runId: string;
  taskId?: string;
  step?: number;
  agentRole?: string;
  content?: string;
  tokenUsage?: TokenUsage;
  artifactId?: string;
  approvalId?: string;
  // Steerable run trace (type === 'run_event' mirrors a persisted RunEvent)
  eventId?: string;
  round?: number;
  parentId?: string | null;
  kind?: RunEventKind;
  title?: string;
  toolName?: string;
  toolArgsSummary?: string;
  resultSummary?: string;
  timestamp: string;
}


export interface SearchResultItem {
  title: string;
  subtitle?: string;
  price?: string;
  details?: string[];
  link?: string;
  imageUrl?: string;
  metadata?: Record<string, string>;
}

export interface HumanInteraction {
  id: string;
  origin?: 'Pipeline' | 'Steerable';
  tenantId: string;
  runId: string;
  taskId: string;
  boardId: string;
  stepIndex: number;
  type: InteractionType;
  status: InteractionStatus;
  actionDescription: string;
  requestedFrom: string[];
  requestedAt: string;
  expiresAt: string;
  respondedBy?: string;
  respondedAt?: string;
  // Selection
  items?: SearchResultItem[];
  selectedIndex?: number;
  // Question
  question?: string;
  questionOptions?: string[];
  answer?: string;
  // Approval
  notes?: string;
  approved?: boolean;
  identityToBeUsed?: string;
}

// ── Live preview (mirrors backend PreviewSession JSON) ─────────────────────────
export type PreviewStatus = 'Provisioning' | 'Running' | 'Failed' | 'Stopped';

export interface PreviewSession {
  id: string;
  boardId: string;
  branch: string;
  status: PreviewStatus;
  url?: string;
  error?: string;
  createdAt: string;
  expiresAt: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Front-end-only models — board views, columns, filters, automations, collab.
// These power the rich Monday.com-style UI. View/column config and the
// collaboration layer are persisted client-side (localStorage) since the backend
// schema does not model them.
// ─────────────────────────────────────────────────────────────────────────────

export type ColumnKind =
  | 'title' | 'status' | 'priority' | 'people' | 'date' | 'timeline'
  | 'number' | 'text' | 'tags' | 'dropdown' | 'progress' | 'rating'
  | 'checkbox' | 'link' | 'dependency' | 'upstream' | 'downstream' | 'tokens' | 'cost' | 'trigger'
  | 'createdAt' | 'lastUpdated' | 'itemId' | 'autoNumber' | 'formula' | 'result';

export type ColumnAggregation =
  | 'none' | 'sum' | 'avg' | 'min' | 'max' | 'median' | 'count' | 'countEmpty' | 'distribution';

export interface ColumnDef {
  id: string;            // stable id; built-ins map to a task field
  kind: ColumnKind;
  title: string;
  width: number;
  hidden?: boolean;
  pinned?: boolean;
  aggregation?: ColumnAggregation;
  /** For custom columns (tags/dropdown/text/number that aren't backed by a task field). */
  custom?: boolean;
  /** dropdown / tags option labels. */
  options?: { label: string; hex: string }[];
  /** formula expression referencing other column ids, e.g. "{tokens} * 0.001". */
  formula?: string;
  /** number column unit suffix. */
  unit?: string;
}

export type ViewKind = 'table' | 'kanban' | 'timeline' | 'calendar' | 'cards' | 'chart' | 'canvas';

export type FilterOperator =
  | 'is' | 'isNot' | 'isEmpty' | 'isNotEmpty' | 'contains' | 'notContains'
  | 'gt' | 'lt' | 'gte' | 'lte' | 'before' | 'after' | 'anyOf';

export interface FilterRule {
  id: string;
  columnId: string;
  operator: FilterOperator;
  value?: string | string[] | number;
}

export interface FilterGroup {
  conjunction: 'and' | 'or';
  rules: FilterRule[];
}

export interface SortRule {
  columnId: string;
  direction: 'asc' | 'desc';
}

export interface ChartConfig {
  type: 'bar' | 'column' | 'pie' | 'line' | 'donut';
  groupBy: string;       // column id
  metric: 'count' | 'sum' | 'avg';
  metricColumnId?: string;
}

export interface ViewDef {
  id: string;
  name: string;
  kind: ViewKind;
  /** column id to group rows by (table/kanban). 'status' by default. */
  groupBy?: string;
  filter?: FilterGroup;
  sorts?: SortRule[];
  /** column id used as the time axis for timeline/calendar. */
  dateColumnId?: string;
  chart?: ChartConfig;
  builtIn?: boolean;
}

// ── Collaboration ─────────────────────────────────────────────────────────────

export interface Person {
  id: string;            // email or role id
  name: string;
  kind: AssigneeType;
  hex: string;
  title?: string;
}

export type CommentKind = 'note' | 'message';
export type NoteType = 'decision' | 'open_question' | 'note';

export interface Comment {
  id: string;
  taskId: string;
  boardId: string;
  kind: CommentKind;
  noteType?: NoteType;          // notes only
  authorId: string;
  body: string;
  mentions: string[];
  reactions?: Record<string, string[]>;   // emoji -> userIds
  createdAt: string;
  updatedAt?: string;
  editedBy?: string;
  deletedAt?: string;
  sharedWithAgent?: boolean;
  sharedAt?: string;
  sharedBy?: string;
}

export type ActivityKind =
  | 'created' | 'status' | 'priority' | 'assignee' | 'due' | 'comment'
  | 'connected' | 'artifact' | 'approval' | 'field';

export interface ActivityEntry {
  id: string;
  taskId: string;
  kind: ActivityKind;
  actorId: string;
  from?: string;
  to?: string;
  field?: string;
  createdAt: string;
}

export type NotificationType = 'approval' | 'completed' | 'failed' | 'agent' | 'mention' | 'blocked' | 'assigned';

export interface AppNotification {
  id: string;
  type: NotificationType;
  title: string;
  subtitle?: string;
  boardId?: string;
  taskId?: string;
  runId?: string;
  timestamp: string;
  sourceEventType: string;
}

// ── Automations ─────────────────────────────────────────────────────────────

export type TriggerType =
  | 'statusChanges' | 'statusBecomes' | 'itemCreated' | 'dateArrives'
  | 'priorityBecomes' | 'personAssigned' | 'artifactUpdated';

export type ActionType =
  | 'notify' | 'setStatus' | 'setPriority' | 'assign' | 'createItem'
  | 'moveToGroup' | 'createApproval' | 'runAgent';

export interface AutomationRecipe {
  id: string;
  enabled: boolean;
  trigger: { type: TriggerType; value?: string };
  conditions: { columnId: string; operator: FilterOperator; value?: string }[];
  actions: { type: ActionType; value?: string; value2?: string }[];
  runs: number;
  createdAt: string;
}

// ── Dashboards ─────────────────────────────────────────────────────────────

export type WidgetKind = 'kpi' | 'chart' | 'battery' | 'table' | 'timeline' | 'text' | 'workload';

export interface DashboardWidget {
  id: string;
  kind: WidgetKind;
  title: string;
  w: number;             // grid span (1-4)
  h: number;             // grid rows
  boardIds?: string[];
  config?: Record<string, unknown>;
}

// ── Code viewer (Spec 2) ───────────────────────────────────────────────
export interface RepoMeta { defaultBranch: string; description?: string | null; private: boolean; }
export interface BranchInfo { name: string; commitSha: string; }
export interface TreeEntry { name: string; path: string; type: 'file' | 'dir'; size: number; }
export interface FileContent { path: string; sha: string; size: number; isBinary: boolean; text: string | null; }
export interface CommitInfo { sha: string; message: string; author: string; date: string; url: string; }
export interface PullRequestInfo { number: number; title: string; state: string; author: string; head: string; base: string; url: string; createdAt: string; }
export interface DiffFile { path: string; status: string; additions: number; deletions: number; isBinary: boolean; patch: string | null; }
export interface CompareResult { headSha: string; filesChanged: number; additions: number; deletions: number; files: DiffFile[]; }
