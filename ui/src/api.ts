import type {
  AnswerView,
  AssignmentView,
  BusSnapshot,
  GoalExplorerResponse,
  GraphQueryResult,
  GraphSnapshot,
  NodeKind,
  QuestionView,
  ResultView,
  SpecView,
  TimelineView,
  WorkflowDetail,
  WorkflowRunSummary,
  WorkflowState,
} from './types';

/** All reads go through the dashboard BFF so the UI has a single origin. */
const dashboardBase = '/api/dashboard';

async function get<T>(path: string): Promise<T> {
  const response = await fetch(path);
  if (!response.ok) {
    throw new Error(`${response.status} ${await response.text()}`);
  }

  return (await response.json()) as T;
}

async function getOptional<T>(path: string): Promise<T | null> {
  const response = await fetch(path);
  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`${response.status} ${await response.text()}`);
  }

  return (await response.json()) as T;
}

export const api = {
  workflows: () => get<{ items: WorkflowState[] }>(`${dashboardBase}/workflows`),

  async detail(id: number): Promise<WorkflowDetail> {
    const workflowPath = `${dashboardBase}/workflows/${id}`;
    const [state, questions, answers, assignments, results, timeline, spec, summary] = await Promise.all([
      get<WorkflowState>(workflowPath),
      get<{ items: QuestionView[] }>(`${workflowPath}/questions?open=false`),
      get<{ items: AnswerView[] }>(`${workflowPath}/answers`),
      get<{ items: AssignmentView[] }>(`${workflowPath}/assignments`),
      get<{ items: ResultView[] }>(`${workflowPath}/results`),
      get<TimelineView>(`${workflowPath}/timeline`),
      getOptional<SpecView>(`${workflowPath}/spec`),
      get<WorkflowRunSummary>(`${workflowPath}/summary`),
    ]);

    return {
      state,
      questions: questions.items,
      answers: answers.items,
      assignments: assignments.items,
      results: results.items,
      timeline,
      spec,
      summary,
    };
  },

  graph: (maxNodes: number, maxEdges: number, newest: boolean) =>
    get<GraphSnapshot>(
      `${dashboardBase}/graph?maxNodes=${maxNodes}&maxEdges=${maxEdges}&newest=${newest}`,
    ),

  workflowSummary: (id: number) => get<WorkflowRunSummary>(`${dashboardBase}/workflows/${id}/summary`),

  bus: () => get<BusSnapshot>(`${dashboardBase}/bus`),
  kinds: () => get<{ items: NodeKind[] }>(`${dashboardBase}/kinds`),

  nodes: (params: {
    kind?: string;
    scope?: string;
    within?: number;
    via?: string;
    descend?: string;
    limit?: number;
  }) => {
    const query = new URLSearchParams({ summary: 'true', limit: String(params.limit ?? 100) });
    if (params.kind) query.set('kind', params.kind);
    if (params.scope) query.set('scope', params.scope);
    if (params.within !== undefined) query.set('within', String(params.within));
    if (params.via) query.set('via', params.via);
    if (params.descend) query.set('descend', params.descend);
    return get<GraphQueryResult>(`${dashboardBase}/nodes?${query.toString()}`);
  },

  goals: (params: { repoId?: number; resolved?: boolean }) => {
    const query = new URLSearchParams();
    if (params.repoId !== undefined) query.set('repoId', String(params.repoId));
    if (params.resolved !== undefined) query.set('resolved', String(params.resolved));
    return get<GoalExplorerResponse>(`${dashboardBase}/goals?${query.toString()}`);
  },

  search: (params: {
    q: string;
    kind?: string;
    scope?: string;
    within?: number;
    via?: string;
    descend?: string;
    limit?: number;
  }) => {
    const query = new URLSearchParams({ q: params.q, summary: 'true', limit: String(params.limit ?? 100) });
    if (params.kind) query.set('kind', params.kind);
    if (params.scope) query.set('scope', params.scope);
    if (params.within !== undefined) query.set('within', String(params.within));
    if (params.via) query.set('via', params.via);
    if (params.descend) query.set('descend', params.descend);
    return get<GraphQueryResult>(`${dashboardBase}/search?${query.toString()}`);
  },


  busStreamUrl: (topics: string) =>
    `${dashboardBase}/bus/stream?topics=${encodeURIComponent(topics)}`,
};
