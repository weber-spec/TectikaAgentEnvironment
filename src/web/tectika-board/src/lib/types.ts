// ─────────────────────────────────────────────────────────────────────────────
// Mirror of C# models — keep in sync with TectikaAgents.Core.Models.
// (Enums are serialized as their string names by the API; see JsonStringEnumConverter.)
// ─────────────────────────────────────────────────────────────────────────────

export type AgentTaskStatus = 'Backlog' | 'InProgress' | 'AwaitingApproval' | 'AwaitingInteraction' | 'Blocked' | 'Review' | 'Done' | 'Failed';
export type TaskPriority = 'Critical' | 'High' | 'Medium' | 'Low';
export type AssigneeType = 'Agent' | 'Human';
export type ArtifactContentType = 'Code' | 'Markdown' | 'Json' | 'Data';
export type ArtifactOrigin = 'Agent' | 'HumanEdit' | 'CliBridge';
export type RunStatus = 'Pending' | 'Running' | 'PausedApproval' | 'AwaitingInteraction' | 'Completed' | 'Failed' | 'Cancelled';
export type TriggerSource = 'Manual' | 'Supervisor' | 'WebhookGitHub' | 'WebhookJira' | 'Schedule' | 'CliBridge';
export type StepType = 'AgentExecution' | 'ApprovalGate' | 'CliBridge';
export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Expired';
export type InteractionType = 'Approval' | 'Selection' | 'Question';
export type InteractionStatus = 'Pending' | 'Responded' | 'Expired';

export type BoardRunPhase =
  | { kind: 'idle' }
  | { kind: 'running'; taskIds: string[] }
  | { kind: 'done'; status: 'AwaitingInteraction' | 'Failed' | 'Completed' };

export interface Board {
  id: string;
  tenantId: string;
  name: string;
  description: string;
  ownerId: string;
  columns: string[];
  createdAt: string;
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
  createdAt: string;
  dueAt?: string;
}

// ── Steerable run trace (mirrors backend RunEvent; Activity tab + chat transcript) ──
export type RunEventKind =
  | 'Thinking' | 'RoundStarted' | 'ToolCall' | 'ToolResult' | 'ArtifactWritten'
  | 'UserMessage' | 'AgentMessage' | 'InteractionRequired' | 'ApprovalRequired'
  | 'RoundCompleted' | 'RunCompleted' | 'RunFailed';

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
  foundryAgentId?: string | null;
  foundryAgentHash?: string | null;
  tools: string[];
  mcpServers: string[];
  permissions: AgentPermissions;
  escalateTo?: string;
  modelOverride?: string;
  createdAt?: string;
  updatedAt?: string;
}

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
  output: number;
  total: number;
}

export interface PipelineStep {
  step: number;
  type: StepType;
  agentRoleId?: string;
  action?: string;
  approvers: string[];
}

export interface StepResult {
  step: number;
  status: RunStatus;
  foundryRunId?: string;
  artifactId?: string;
  tokenUsage: TokenUsage;
  durationMs: number;
  completedAt?: string;
  error?: string;
}

export interface WorkflowRun {
  id: string;
  tenantId: string;
  taskId: string;
  pipelineDefinition: PipelineStep[];
  currentStep: number;
  status: RunStatus;
  steps: StepResult[];
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

export interface Approval {
  id: string;
  tenantId: string;
  runId: string;
  taskId: string;
  stepIndex: number;
  requestedAt: string;
  expiresAt: string;
  requestedFrom: string[];
  status: ApprovalStatus;
  approvedBy?: string;
  approvedAt?: string;
  notes?: string;
  actionDescription: string;
  identityToBeUsed?: string;
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

export interface Comment {
  id: string;
  taskId: string;
  authorId: string;
  body: string;
  mentions: string[];
  createdAt: string;
  reactions?: Record<string, string[]>; // emoji -> userIds
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
