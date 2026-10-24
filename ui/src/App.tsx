import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from './api';
import type {
  FindingSeverity,
  WorkflowDetail,
  WorkflowState,
  WorkflowStatus,
} from './types';

const busUrl =
  (window as unknown as { ANIMA_BUS_URL?: string }).ANIMA_BUS_URL ??
  `${location.protocol}//${location.hostname}:7412`;

const statusLabels: Record<WorkflowStatus, string> = {
  analysis: 'Analysis',
  awaitingAnswers: 'Awaiting answers',
  specReady: 'Spec ready',
  implementing: 'Implementing',
  implementationComplete: 'Implementation complete',
  mergeComplete: 'Merged',
  completed: 'Completed',
  failed: 'Failed',
};

function formatTime(value: string): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function statusClass(status: WorkflowStatus): string {
  if (status === 'failed') return 'chip chip-failed';
  if (status === 'completed') return 'chip chip-done';
  if (status === 'awaitingAnswers') return 'chip chip-warn';
  if (status.startsWith('implementation') || status === 'specReady') return 'chip chip-active';
  return 'chip';
}

function severityClass(severity: FindingSeverity): string {
  return severity === 'error' ? 'finding finding-error' : severity === 'warning' ? 'finding finding-warn' : 'finding';
}

export default function App() {
  const [workflows, setWorkflows] = useState<WorkflowState[]>([]);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<WorkflowDetail | null>(null);
  const [live, setLive] = useState<'connecting' | 'live' | 'offline'>('connecting');
  const [error, setError] = useState<string | null>(null);
  const selectedRef = useRef<number | null>(null);
  const refreshTimer = useRef<number | undefined>(undefined);
  const listRequest = useRef(0);
  const detailRequest = useRef(0);
  selectedRef.current = selectedId;

  const loadList = useCallback(async () => {
    const request = ++listRequest.current;
    try {
      const response = await api.workflows();
      if (request !== listRequest.current) return;
      setWorkflows(response.items);
      setError(null);
      if (selectedRef.current === null && response.items.length > 0) {
        setSelectedId(response.items[0].id);
      }
    } catch (exception) {
      if (request === listRequest.current) {
        setError(String(exception));
      }
    }
  }, []);

  const loadDetail = useCallback(async (id: number) => {
    const request = ++detailRequest.current;
    try {
      const next = await api.detail(id);
      if (request !== detailRequest.current) return;
      setDetail(next);
      setError(null);
    } catch (exception) {
      if (request === detailRequest.current) {
        setError(String(exception));
      }
    }
  }, []);

  useEffect(() => {
    void loadList();
  }, [loadList]);

  useEffect(() => {
    if (selectedId !== null) {
      void loadDetail(selectedId);
    }
  }, [selectedId, loadDetail]);

  useEffect(() => {
    const source = new EventSource(`${busUrl}/api/bus/stream?topics=anima.**`);

    source.onopen = () => setLive('live');
    source.onerror = () => setLive('offline');

    const onMessage = (event: MessageEvent<string>) => {
      try {
        const envelope = JSON.parse(event.data) as {
          typeName?: string;
          payloadJson?: Record<string, unknown>;
        };
        const type = envelope.typeName ?? '';
        if (!type.includes('WorkflowEvent') && !type.includes('GraphEvent')) return;

        const eventWorkflowId =
          envelope.payloadJson?.workflowId ?? envelope.payloadJson?.nodeId;
        window.clearTimeout(refreshTimer.current);
        refreshTimer.current = window.setTimeout(() => {
          void loadList();
          const current = selectedRef.current;
          if (
            current !== null &&
            (eventWorkflowId === undefined || String(eventWorkflowId) === String(current))
          ) {
            void loadDetail(current);
          }
        }, 250);
      } catch {
        // Ignore malformed frames; the polling fallback still works.
      }
    };

    source.addEventListener('message', onMessage);
    return () => {
      source.removeEventListener('message', onMessage);
      source.close();
      window.clearTimeout(refreshTimer.current);
    };
  }, [loadList, loadDetail]);

  useEffect(() => {
    if (live === 'live') {
      return;
    }

    const timer = window.setInterval(() => {
      void loadList();
      if (selectedRef.current !== null) {
        void loadDetail(selectedRef.current);
      }
    }, 10_000);
    return () => window.clearInterval(timer);
  }, [live, loadList, loadDetail]);

  const answersByQuestion = new Map(detail?.answers.map((answer) => [answer.questionId, answer]) ?? []);
  const openQuestions = detail?.questions.filter((question) => !question.answered) ?? [];

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>Anima</h1>
          <p>Harness-driven workflow state</p>
        </div>
        <div className="topbar-actions">
          <span className={`live live-${live}`}>
            {live === 'live' ? 'live' : live === 'offline' ? 'polling' : 'connecting'}
          </span>
          <button className="button" onClick={() => void loadList()}>
            Refresh
          </button>
        </div>
      </header>

      {error && <div className="error">{error}</div>}

      <div className="layout">
        <aside className="sidebar">
          <h2>Workflows</h2>
          {workflows.length === 0 && <p className="muted">No workflows yet.</p>}
          <div className="workflow-list">
            {workflows.map((workflow) => (
              <button
                key={workflow.id}
                className={`workflow-item ${workflow.id === selectedId ? 'selected' : ''}`}
                onClick={() => setSelectedId(workflow.id)}
              >
                <span className="workflow-name">{workflow.name}</span>
                <span className={statusClass(workflow.status)}>
                  {statusLabels[workflow.status] ?? workflow.status}
                </span>
                <span className="muted small">
                  #{workflow.id} · {workflow.completedTaskCount}/{workflow.taskCount} tasks
                </span>
              </button>
            ))}
          </div>
        </aside>

        <main className="content">
          {!detail && <p className="muted">Select a workflow to inspect it.</p>}
          {detail && (
            <>
              <section className="panel">
                <div className="panel-heading">
                  <div>
                    <h2>{detail.state.name}</h2>
                    <p className="muted">
                      #{detail.state.id} · {detail.state.repoPath} · base {detail.state.baseRef}
                    </p>
                  </div>
                  <span className={statusClass(detail.state.status)}>
                    {statusLabels[detail.state.status] ?? detail.state.status}
                  </span>
                </div>
                <p className="requirement">{detail.state.requirement || 'No requirement recorded.'}</p>
                <div className="stats">
                  <div className="stat"><strong>{detail.state.openQuestionCount}</strong><span>open questions</span></div>
                  <div className="stat"><strong>{detail.state.taskCount}</strong><span>tasks</span></div>
                  <div className="stat"><strong>{detail.state.completedTaskCount}</strong><span>implemented</span></div>
                  <div className="stat"><strong>{detail.spec?.version ?? '—'}</strong><span>spec version</span></div>
                  <div className="stat"><strong>{formatTime(detail.state.updatedAt)}</strong><span>updated</span></div>
                </div>
              </section>

              <section className="panel">
                <h2>Questions</h2>
                {detail.questions.length === 0 && <p className="muted">No questions recorded.</p>}
                <div className="question-list">
                  {detail.questions.map((question) => {
                    const answer = answersByQuestion.get(question.id);
                    return (
                      <article key={question.id} className={`question ${question.answered ? 'answered' : 'open'}`}>
                        <div className="question-heading">
                          <strong>{question.text}</strong>
                          <span className={question.answered ? 'chip chip-done' : 'chip chip-warn'}>
                            {question.answered ? 'answered' : 'open'}
                          </span>
                        </div>
                        {question.rationale && <p className="muted">{question.rationale}</p>}
                        {answer && <p className="answer">{answer.text}</p>}
                        <span className="muted small">{formatTime(question.askedAt)}</span>
                      </article>
                    );
                  })}
                </div>
                {openQuestions.length > 0 && (
                  <p className="muted">The harness is waiting for the user to answer {openQuestions.length} question(s).</p>
                )}
              </section>

              <section className="panel">
                <h2>Refined spec</h2>
                {!detail.spec && <p className="muted">No spec posted yet.</p>}
                {detail.spec && (
                  <>
                    <div className="panel-subheading">
                      <span>{detail.spec.name}</span>
                      <span className="muted small">v{detail.spec.version} · {formatTime(detail.spec.updatedAt)}</span>
                    </div>
                    <details>
                      <summary>Show spec markdown</summary>
                      <pre className="markdown">{detail.spec.markdown}</pre>
                    </details>
                    <div className="task-grid">
                      {detail.spec.tasks.map((task) => (
                        <article key={task.key} className="task">
                          <div className="question-heading">
                            <strong>{task.title}</strong>
                            <span className="chip">{task.key}</span>
                          </div>
                          <p className="muted">{task.description}</p>
                          {task.acceptanceCriteria.length > 0 && (
                            <div>
                              <span className="label">Acceptance</span>
                              <ul>{task.acceptanceCriteria.map((item) => <li key={item}>{item}</li>)}</ul>
                            </div>
                          )}
                          {task.verifyCommands.length > 0 && (
                            <div>
                              <span className="label">Verify</span>
                              <ul>{task.verifyCommands.map((item) => <li key={item}><code>{item}</code></li>)}</ul>
                            </div>
                          )}
                        </article>
                      ))}
                    </div>
                  </>
                )}
              </section>

              <section className="panel">
                <h2>Assignments</h2>
                {detail.assignments.length === 0 && <p className="muted">No worktrees registered.</p>}
                <div className="assignment-grid">
                  {detail.assignments.map((assignment) => (
                    <article key={assignment.id} className="assignment">
                      <div className="question-heading">
                        <strong>{assignment.taskKey}</strong>
                        <span className="chip">{assignment.agentId}</span>
                      </div>
                      <p className="mono small">{assignment.branch}</p>
                      <p className="mono small muted">{assignment.worktreePath}</p>
                      <span className="muted small">{assignment.status} · {formatTime(assignment.updatedAt)}</span>
                    </article>
                  ))}
                </div>
              </section>

              <section className="panel">
                <h2>Results</h2>
                {detail.results.length === 0 && <p className="muted">No results posted.</p>}
                <div className="result-list">
                  {detail.results.map((result) => (
                    <article key={result.id} className={`result ${result.success ? 'success' : 'failure'}`}>
                      <div className="question-heading">
                        <strong>
                          {result.kind}{result.taskKey ? ` · ${result.taskKey}` : ''}
                        </strong>
                        <span className={result.success ? 'chip chip-done' : 'chip chip-failed'}>
                          {result.success ? 'success' : 'failure'}
                        </span>
                      </div>
                      <p>{result.summary}</p>
                      {result.agentId && <p className="muted small">agent {result.agentId}</p>}
                      {result.changedFiles.length > 0 && (
                        <p className="mono small">changed: {result.changedFiles.join(', ')}</p>
                      )}
                      {result.conflicts.length > 0 && (
                        <p className="mono small">conflicts: {result.conflicts.join(', ')}</p>
                      )}
                      {result.findings.length > 0 && (
                        <div className="findings">
                          {result.findings.map((finding, index) => (
                            <div key={index} className={severityClass(finding.severity)}>
                              <strong>{finding.severity}</strong> {finding.message}
                              {finding.path && <span className="mono small"> {finding.path}{finding.line ? `:${finding.line}` : ''}</span>}
                            </div>
                          ))}
                        </div>
                      )}
                      <span className="muted small">{formatTime(result.createdAt)}</span>
                    </article>
                  ))}
                </div>
              </section>

              <section className="panel">
                <h2>Phases</h2>
                <ol className="timeline">
                  {detail.timeline.phases.map((phase) => (
                    <li key={phase.id}>
                      <span className={statusClass(phase.status)}>{phase.name}</span>
                      <span className="muted small"> {formatTime(phase.createdAt)}</span>
                      {phase.summary && <p className="muted small">{phase.summary}</p>}
                    </li>
                  ))}
                </ol>
              </section>
            </>
          )}
        </main>
      </div>
    </div>
  );
}
