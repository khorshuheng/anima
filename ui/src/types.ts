export type WorkflowStatus =
  | 'analysis'
  | 'awaitingAnswers'
  | 'specReady'
  | 'assigned'
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
  messageId: string | null;
  acknowledgedAt: string | null;
  runId: string | null;
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
  headSha: string | null;
  branch: string | null;
  baseRef: string | null;
  runId: string | null;
  testCommands: string[];
  artifactPaths: string[];
  createdAt: string;
}

export interface SummaryTaskBreakdown {
  total: number;
  implementation: number;
  merge: number;
  review: number;
  verification: number;
  documentation: number;
}

export interface SummaryAgents {
  recordedAgents: number;
  recordedRuns: number;
  agentIds: string[];
}

export interface SummaryResults {
  total: number;
  implementation: number;
  merge: number;
  review: number;
  verification: number;
  findings: number;
  errors: number;
  warnings: number;
}

export interface SummaryEvidence {
  implementationResults: number;
  implementationWithHeadSha: number;
  implementationWithTests: number;
  reviewResults: number;
  reviewWithHeadSha: number;
  reviewWithTests: number;
}

export interface SummaryReview {
  index: number;
  resultId: number;
  createdAt: string;
  success: boolean;
  agentId: string | null;
  headSha: string | null;
  testCommands: string[];
  findingCount: number;
  summary: string;
}

export interface SummaryAssignment {
  id: number;
  taskKey: string;
  agentId: string;
  status: string;
  messageId: string | null;
  acknowledgedAt: string | null;
  runId: string | null;
  updatedAt: string;
}

export interface SummaryPhase {
  id: number;
  name: string;
  status: WorkflowStatus;
  summary: string | null;
  createdAt: string;
}

export interface WorkflowRunSummary {
  id: number;
  name: string;
  status: WorkflowStatus;
  createdAt: string;
  updatedAt: string;
  durationMs: number;
  specVersion: number | null;
  specHash: string | null;
  tasks: SummaryTaskBreakdown;
  agents: SummaryAgents;
  results: SummaryResults;
  evidence: SummaryEvidence;
  reviewIterations: SummaryReview[];
  assignments: SummaryAssignment[];
  phases: SummaryPhase[];
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
  summary: WorkflowRunSummary;
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

export interface NodeKind {
  id: number;
  name: string;
  description: string;
  required_props: string[];
  traits: string[];
  source: string;
  deprecated: boolean;
}

export interface EdgeType {
  id: number;
  name: string;
  description: string;
  resolves: boolean;
  single_outgoing: boolean;
  symmetric: boolean;
  acyclic: boolean;
  inverse_name: string | null;
  source_kinds: string[];
  destination_kinds: string[];
  pairs: string[][];
  traits: string[];
  source: string;
  deprecated: boolean;
}

export interface Vocabulary {
  node_kinds: NodeKind[];
  edge_types: EdgeType[];
  traits: string[];
}

export interface GraphQueryResult {
  items: GraphNode[];
  total: number;
  limit: number;
  offset: number;
}

export interface GoalExplorerItem {
  id: number;
  name: string;
  description: string;
  resolved: boolean;
  taskCount: number;
  resolvedTaskCount: number;
  workflowStatus: string | null;
  completionSource: 'goal' | 'workflow' | 'tasks' | 'open';
}

export interface GoalExplorerResponse {
  items: GoalExplorerItem[];
  total: number;
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
  /** Topics the bus still tracks; topics past its cap are only counted in the totals. */
  trackedTopics: number;
  /** True when `topics` is the busiest slice of `trackedTopics`. */
  topicsTruncated: boolean;
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
