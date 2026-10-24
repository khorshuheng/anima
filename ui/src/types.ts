export type WorkflowStatus =
  | 'analysis'
  | 'awaitingAnswers'
  | 'specReady'
  | 'implementing'
  | 'implementationComplete'
  | 'mergeComplete'
  | 'completed'
  | 'failed';

export interface WorkflowState {
  id: number;
  goalId: number;
  name: string;
  requirement: string;
  status: WorkflowStatus;
  repoPath: string;
  baseRef: string;
  specId: number | null;
  openQuestionCount: number;
  taskCount: number;
  completedTaskCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface QuestionView {
  id: number;
  workflowId: number;
  text: string;
  rationale: string | null;
  answered: boolean;
  askedAt: string;
}

export interface AnswerView {
  id: number;
  questionId: number;
  text: string;
  answeredAt: string;
}

export interface SpecTaskView {
  key: string;
  title: string;
  description: string;
  acceptanceCriteria: string[];
  verifyCommands: string[];
  nodeId: number;
  role?: 'implementation' | 'merge' | 'review' | 'verification' | 'documentation';
}

export interface SpecView {
  id: number;
  workflowId: number;
  name: string;
  markdown: string;
  tasks: SpecTaskView[];
  version: number;
  updatedAt: string;
}

export interface AssignmentView {
  id: number;
  taskKey: string;
  agentId: string;
  worktreePath: string;
  branch: string;
  status: string;
  updatedAt: string;
}

export type ResultKind = 'implementation' | 'merge' | 'review' | 'verification';
export type FindingSeverity = 'info' | 'warning' | 'error';

export interface FindingView {
  severity: FindingSeverity;
  message: string;
  path: string | null;
  line: number | null;
}

export interface ResultView {
  id: number;
  taskKey: string;
  kind: ResultKind;
  success: boolean;
  summary: string;
  changedFiles: string[];
  conflicts: string[];
  findings: FindingView[];
  agentId: string | null;
  createdAt: string;
}

export interface PhaseView {
  id: number;
  name: string;
  status: WorkflowStatus;
  summary: string | null;
  createdAt: string;
}

export interface TimelineView {
  phases: PhaseView[];
  results: ResultView[];
}

export interface WorkflowDetail {
  state: WorkflowState;
  questions: QuestionView[];
  answers: AnswerView[];
  spec: SpecView | null;
  assignments: AssignmentView[];
  results: ResultView[];
  timeline: TimelineView;
}

export interface GraphNode {
  id: number;
  kind: string;
  name: string;
  description: string;
  resolved: boolean;
}

export interface GraphEdge {
  id: number;
  source: number;
  destination: number;
  edgeType: string;
}

export interface GraphSnapshot {
  nodes: GraphNode[];
  edges: GraphEdge[];
  nodeCount: number;
  edgeCount: number;
  truncated: boolean;
}

export interface BusTopicSnapshot {
  topic: string;
  publishedCount: number;
  lastTimestamp: string | null;
  typeNames: string[];
  senders: string[];
}

export interface BusSubscriptionSnapshot {
  id: string;
  name: string;
  pattern: string;
  delivered: number;
  dropped: number;
  failed: number;
  lastActivity: string | null;
}

export interface BusSnapshot {
  topics: BusTopicSnapshot[];
  subscriptions: BusSubscriptionSnapshot[];
  totalPublished: number;
  totalDelivered: number;
  totalDropped: number;
  totalFailed: number;
}

/** One `anima.**` envelope as delivered by the dashboard SSE proxy. */
export interface BusStreamEvent {
  messageId: string;
  topic: string;
  typeName: string;
  timestamp: string;
  sender?: string | null;
  recipient?: string | null;
  correlationId?: string | null;
  payloadJson?: unknown;
}
