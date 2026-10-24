import type {
  AnswerView,
  AssignmentView,
  BusSnapshot,
  GraphSnapshot,
  QuestionView,
  ResultView,
  SpecView,
  TimelineView,
  WorkflowDetail,
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
    const [state, questions, answers, assignments, results, timeline, spec] = await Promise.all([
      get<WorkflowState>(workflowPath),
      get<{ items: QuestionView[] }>(`${workflowPath}/questions?open=false`),
      get<{ items: AnswerView[] }>(`${workflowPath}/answers`),
      get<{ items: AssignmentView[] }>(`${workflowPath}/assignments`),
      get<{ items: ResultView[] }>(`${workflowPath}/results`),
      get<TimelineView>(`${workflowPath}/timeline`),
      getOptional<SpecView>(`${workflowPath}/spec`),
    ]);

    return {
      state,
      questions: questions.items,
      answers: answers.items,
      assignments: assignments.items,
      results: results.items,
      timeline,
      spec,
    };
  },

  graph: (maxNodes: number, maxEdges: number, newest: boolean) =>
    get<GraphSnapshot>(
      `${dashboardBase}/graph?maxNodes=${maxNodes}&maxEdges=${maxEdges}&newest=${newest}`,
    ),

  bus: () => get<BusSnapshot>(`${dashboardBase}/bus`),

  busStreamUrl: (topics: string) =>
    `${dashboardBase}/bus/stream?topics=${encodeURIComponent(topics)}`,
};
