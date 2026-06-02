// Mirror of C# models — keep in sync with TectikaAgents.Core.Models

export type AgentTaskStatus = 'Backlog' | 'InProgress' | 'AwaitingApproval' | 'Blocked' | 'Review' | 'Done' | 'Failed';
export type TaskPriority = 'Critical' | 'High' | 'Medium' | 'Low';
export type AssigneeType = 'Agent' | 'Human';
export type ArtifactContentType = 'Code' | 'Markdown' | 'Json' | 'Data';
export type ArtifactOrigin = 'Agent' | 'HumanEdit' | 'CliBridge';
export type RunStatus = 'Pending' | 'Running' | 'PausedApproval' | 'Completed' | 'Failed' | 'Cancelled';

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
  currentArtifactId?: string;
  canvasPosition?: CanvasPosition;
  upstreamTaskIds: string[];
  downstreamTaskIds: string[];
  humanAuditorId?: string;
  createdAt: string;
}

export interface AgentRole {
  id: string;
  tenantId: string;
  displayName: string;
  systemPrompt: string;
  foundryAgentId?: string;
  tools: string[];
  mcpServers: string[];
  permissions: {
    canPushCode: boolean;
    canDeploy: boolean;
    requiresOboFor: string[];
    requiresApprovalFor: string[];
  };
  escalateTo?: string;
  modelOverride?: string;
}

export interface Artifact {
  id: string;
  taskId: string;
  runId?: string;
  version: number;
  contentType: ArtifactContentType;
  content: string;
  inputContext: {
    upstreamArtifacts: Array<{ taskId: string; artifactId: string; version: number }>;
    humanContext?: string;
  };
  internalLogs: string[];
  origin: ArtifactOrigin;
  updatedAt: string;
}

export interface AgentEvent {
  type: string;
  runId: string;
  taskId?: string;
  step?: number;
  agentRole?: string;
  content?: string;
  tokenUsage?: { input: number; output: number; total: number };
  artifactId?: string;
  approvalId?: string;
  timestamp: string;
}

export interface Approval {
  id: string;
  runId: string;
  taskId: string;
  stepIndex: number;
  requestedAt: string;
  expiresAt: string;
  requestedFrom: string[];
  status: 'Pending' | 'Approved' | 'Rejected' | 'Expired';
  approvedBy?: string;
  approvedAt?: string;
  notes?: string;
  actionDescription: string;
  identityToBeUsed?: string;
}
