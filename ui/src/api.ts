import type {
  AnswerView,
  AssignmentView,
  QuestionView,
  ResultView,
  SpecView,
  TimelineView,
  WorkflowDetail,
  WorkflowState,
} from './types';

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
  workflows: () => get<{ items: WorkflowState[] }>('/api/workflows'),

  async detail(id: number): Promise<WorkflowDetail> {
    const [state, questions, answers, assignments, results, timeline, spec] = await Promise.all([
      get<WorkflowState>(`/api/workflows/${id}`),
      get<{ items: QuestionView[] }>(`/api/workflows/${id}/questions?open=false`),
      get<{ items: AnswerView[] }>(`/api/workflows/${id}/answers`),
      get<{ items: AssignmentView[] }>(`/api/workflows/${id}/assignments`),
      get<{ items: ResultView[] }>(`/api/workflows/${id}/results`),
      get<TimelineView>(`/api/workflows/${id}/timeline`),
      getOptional<SpecView>(`/api/workflows/${id}/spec`),
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
};
