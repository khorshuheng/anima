import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Network, type Edge, type Node, type Options } from 'vis-network/standalone';
import { api } from './api';
import type { GoalExplorerItem, GraphNode, GraphSnapshot, NodeKind } from './types';

/** Mirrors the knowledge snapshot defaults (Anima.Knowledge.GraphSnapshotQuery). */
const maxNodes = 500;
const maxEdges = 1000;
/** Walk the graph newest first so the active workflow is inside the cap. */
const newestWindow = true;
const resultLimit = 100;

const kindPalette = ['#58a6ff', '#3fb950', '#bc8cff', '#d29922', '#f85149', '#39c5cf', '#db6d28', '#8b949e'];

const kindColors: Record<string, string> = {
  goal: '#3fb950',
  workflow: '#39c5cf',
  spec: '#bc8cff',
  task: '#58a6ff',
  question: '#d29922',
  answer: '#d29922',
  phase: '#8b949e',
  result: '#58a6ff',
  change: '#bc8cff',
  decision: '#d2a8ff',
  bug: '#f85149',
  test: '#3fb950',
  agent: '#db6d28',
  worktree: '#db6d28',
};

function kindColor(kind: string): string {
  const known = kindColors[kind];
  if (known) return known;

  let hash = 0;
  for (const char of kind) {
    hash = (hash * 31 + char.charCodeAt(0)) % 997;
  }

  return kindPalette[hash % kindPalette.length];
}

const networkOptions: Options = {
  autoResize: true,
  physics: {
    solver: 'forceAtlas2Based',
    forceAtlas2Based: {
      gravitationalConstant: -60,
      centralGravity: 0.005,
      springLength: 120,
      springConstant: 0.06,
    },
    stabilization: { enabled: true, iterations: 200, updateInterval: 25 },
  },
  interaction: {
    hover: true,
    tooltipDelay: 200,
    navigationButtons: true,
    keyboard: false,
  },
  nodes: {
    shape: 'dot',
    size: 13,
    borderWidth: 1,
    font: { color: '#e6edf3', size: 12, strokeWidth: 0 },
  },
  edges: {
    arrows: { to: { enabled: true, scaleFactor: 0.55 } },
    color: { color: '#3d4b5e', highlight: '#58a6ff', hover: '#58a6ff' },
    selectionWidth: 2,
    smooth: { enabled: true, type: 'continuous', roundness: 0.2 },
  },
};

interface GraphTabProps {
  /** Incremented by the shell on live events and polling ticks. */
  revision: number;
}

export function GraphTab({ revision }: GraphTabProps) {
  const [graph, setGraph] = useState<GraphSnapshot | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [selectedResult, setSelectedResult] = useState<GraphNode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('goal');
  const [scope, setScope] = useState('all');
  const [nodeKinds, setNodeKinds] = useState<NodeKind[]>([]);
  const [results, setResults] = useState<GraphNode[]>([]);
  const [resultTotal, setResultTotal] = useState(0);
  const [resultBusy, setResultBusy] = useState(false);
  const [resultError, setResultError] = useState<string | null>(null);
  const [showFilteredGraph, setShowFilteredGraph] = useState(true);
  const [workflowStatuses, setWorkflowStatuses] = useState<Record<number, string>>({});
  const [workflowStatusesReady, setWorkflowStatusesReady] = useState(false);
  const [repos, setRepos] = useState<GraphNode[]>([]);
  const [repoId, setRepoId] = useState('');
  const [goalDetails, setGoalDetails] = useState<Record<number, GoalExplorerItem>>({});
  const goalDetailsRef = useRef<Record<number, GoalExplorerItem>>({});
  const [goalContext, setGoalContext] = useState<GraphNode | null>(null);
  const [goalTasks, setGoalTasks] = useState<GraphNode[]>([]);
  const [goalTasksLoading, setGoalTasksLoading] = useState(false);
  const [goalTaskGraph, setGoalTaskGraph] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const networkRef = useRef<Network | null>(null);
  const requestRef = useRef(0);
  const resultRequestRef = useRef(0);

  const load = useCallback(async () => {
    const request = ++requestRef.current;
    try {
      const snapshot = await api.graph(maxNodes, maxEdges, newestWindow);
      if (request !== requestRef.current) return;
      setGraph(snapshot);
      setError(null);
    } catch (exception) {
      if (request === requestRef.current) {
        setError(String(exception));
      }
    }
  }, []);

  const resolvedFor = useCallback((node: GraphNode) => {
    return node.resolved ||
      goalDetailsRef.current[node.id]?.resolved === true ||
      (node.kind === 'goal' && workflowStatuses[node.id] === 'completed');
  }, [workflowStatuses]);

  const loadResults = useCallback(async () => {
    if (!workflowStatusesReady) return;
    const request = ++resultRequestRef.current;
    setResultBusy(true);
    try {
      const trimmed = query.trim();
      if (kind === 'goal' && trimmed.length === 0) {
        const response = await api.goals({
          repoId: repoId ? Number(repoId) : undefined,
          resolved: scope === 'all' ? undefined : scope === 'resolved',
        });
        if (request !== resultRequestRef.current) return;
        const details = Object.fromEntries(response.items.map((item) => [item.id, item]));
        goalDetailsRef.current = details;
        setGoalDetails(details);
        setResults(response.items.map((item) => ({
          id: item.id,
          kind: 'goal',
          name: item.name,
          description: item.description,
          resolved: item.resolved,
        })));
        setResultTotal(response.total);
        setResultError(null);
        return;
      }

      const serverScope = kind === 'goal' && scope !== 'all' ? 'all' : scope;
      const repoScope = repoId ? Number(repoId) : undefined;
      const response = trimmed
        ? await api.search({
          q: trimmed,
          kind,
          scope: serverScope,
          within: repoScope,
          via: repoScope === undefined ? undefined : 'belongs_to',
          descend: repoScope === undefined ? undefined : 'part_of',
          limit: resultLimit,
        })
        : await api.nodes({ kind, scope: serverScope, limit: resultLimit });
      if (request !== resultRequestRef.current) return;
      const filtered = kind === 'goal' && scope !== 'all'
        ? response.items.filter((node) => scope === 'resolved' ? resolvedFor(node) : !resolvedFor(node))
        : response.items;
      setResults(filtered);
      setResultTotal(kind === 'goal' && scope !== 'all' ? filtered.length : response.total);
      setResultError(null);
    } catch (exception) {
      if (request === resultRequestRef.current) {
        setResultError(String(exception));
      }
    } finally {
      if (request === resultRequestRef.current) {
        setResultBusy(false);
      }
    }
  }, [kind, query, repoId, resolvedFor, scope, workflowStatusesReady]);

  useEffect(() => {
    void load();
  }, [load, revision]);

  useEffect(() => {
    let active = true;
    setWorkflowStatusesReady(false);
    api.workflows()
      .then((response) => {
        if (!active) return;
        setWorkflowStatuses(Object.fromEntries(
          response.items.map((workflow) => [workflow.goalId, workflow.status]),
        ));
        setWorkflowStatusesReady(true);
      })
      .catch((exception) => {
        if (active) {
          setResultError(String(exception));
          setWorkflowStatusesReady(true);
        }
      });
    return () => {
      active = false;
    };
  }, [revision]);

  useEffect(() => {
    void loadResults();
  }, [loadResults, revision]);

  useEffect(() => {
    let active = true;
    api.kinds()
      .then((response) => {
        if (active) setNodeKinds(response.items);
      })
      .catch((exception) => {
        if (active) setResultError(String(exception));
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;
    api.nodes({ kind: 'repo', scope: 'all', limit: 20 })
      .then((response) => {
        if (!active) return;
        setRepos(response.items);
        const first = response.items[0];
        if (first) setRepoId(String(first.id));
      })
      .catch((exception) => {
        if (active) setResultError(String(exception));
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (!goalContext) {
      setGoalTasks([]);
      setGoalTaskGraph(false);
      return;
    }

    let active = true;
    setGoalTaskGraph(false);
    setGoalTasksLoading(true);
    api.nodes({
      within: goalContext.id,
      via: 'part_of',
      descend: 'part_of',
      kind: 'task',
      scope: 'all',
      limit: 500,
    })
      .then((response) => {
        if (active) setGoalTasks(response.items);
      })
      .catch((exception) => {
        if (active) setResultError(String(exception));
      })
      .finally(() => {
        if (active) setGoalTasksLoading(false);
      });
    return () => {
      active = false;
    };
  }, [goalContext]);

  useEffect(() => {
    if (selectedId === null) return;
    const node = graph?.nodes.find((candidate) => candidate.id === selectedId);
    if (node?.kind === 'goal') {
      setGoalContext(node);
    }
  }, [graph, selectedId]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const network = new Network(container, { nodes: [], edges: [] }, networkOptions);
    networkRef.current = network;
    network.on('selectNode', (params) => {
      const picked = params?.nodes?.[0];
      if (picked !== undefined) {
        setSelectedId(Number(picked));
        setSelectedResult(null);
        network.focus(Number(picked), { scale: 1.15, animation: true });
      }
    });
    network.on('deselectNode', () => {
      setSelectedId(null);
      setSelectedResult(null);
    });

    return () => {
      network.destroy();
      networkRef.current = null;
    };
  }, []);

  const filterActive = query.trim().length > 0 || kind !== '' || scope !== 'all';
  const resultIds = new Set(results.map((node) => node.id));
  const goalTaskActive = goalTaskGraph && goalContext !== null;
  const goalTaskIds = new Set<number>([
    ...(goalContext ? [goalContext.id] : []),
    ...goalTasks.map((task) => task.id),
  ]);
  const displayNodes = graph
    ? goalTaskActive
      ? graph.nodes.filter((node) => goalTaskIds.has(node.id))
      : filterActive && showFilteredGraph
        ? graph.nodes.filter((node) => resultIds.has(node.id))
        : graph.nodes
    : [];
  const displayNodeIds = new Set(displayNodes.map((node) => node.id));
  const displayEdges = graph
    ? graph.edges.filter((edge) => displayNodeIds.has(edge.source) && displayNodeIds.has(edge.destination))
    : [];

  // Replace the data set on refresh instead of recreating the network, so pan
  // and zoom survive live updates.
  useEffect(() => {
    if (!networkRef.current) return;

    const nodes: Node[] = displayNodes.map((node) => {
      const color = kindColor(node.kind);
      return {
        id: node.id,
        label: node.name,
        title: `${node.kind} · #${node.id}\n${node.description}`,
        color: { background: color, border: color, highlight: { background: color, border: '#ffffff' } },
        borderWidth: resolvedFor(node) ? 1 : 3,
      };
    });

    const edges: Edge[] = displayEdges.map((edge) => ({
      id: edge.id,
      from: edge.source,
      to: edge.destination,
      title: edge.edgeType,
    }));

    // Clear first so filtered views cannot leave stale physics nodes behind.
    networkRef.current.setData({ nodes: [], edges: [] });
    networkRef.current.setData({ nodes, edges });
  }, [displayEdges, displayNodes, resolvedFor]);

  const selectedNode: GraphNode | null =
    selectedResult ??
    (selectedId === null ? null : graph?.nodes.find((node) => node.id === selectedId) ?? null);

  const graphKinds = [...new Set(displayNodes.map((node) => node.kind))]
    .sort((left, right) => left.localeCompare(right));

  const nodeLabel = graph
    ? goalTaskActive
      ? `goal tasks: ${goalTasks.length} tasks · ${displayNodes.length} nodes · ${displayEdges.length} edges`
      : filterActive && showFilteredGraph
        ? `matches: ${displayNodes.length} nodes · ${displayEdges.length} edges · source: ${graph.nodes.length}/${graph.nodeCount}`
        : `showing ${graph.nodes.length} of ${graph.nodeCount} nodes · ${graph.edges.length} of ${graph.edgeCount} edges`
    : 'loading graph';

  const submitSearch = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    void loadResults();
  };

  const focusNode = (id: number) => {
    const network = networkRef.current;
    if (!network || !displayNodeIds.has(id)) return;
    network.selectNodes([id]);
    network.focus(id, { scale: 1.25, animation: true });
  };

  const selectResult = (node: GraphNode) => {
    setSelectedResult(node);
    setSelectedId(null);
    if (node.kind === 'goal') {
      setGoalContext(node);
    }
    if (displayNodeIds.has(node.id)) {
      focusNode(node.id);
    } else {
      networkRef.current?.selectNodes([]);
    }
  };

  return (
    <div className="graph-tab">
      {error && <div className="error">{error}</div>}

      {graph?.truncated && (
        <div className="banner">
          <strong>Graph truncated.</strong> The snapshot windows the graph from the newest ids: caps are{' '}
          {maxNodes} nodes and {maxEdges} edges, and edges are only included when both endpoints are in the
          window. Showing the newest {graph.nodes.length} of {graph.nodeCount} nodes ({graph.edges.length} of{' '}
          {graph.edgeCount} edges), so older nodes are hidden.
        </div>
      )}

      <section className="panel graph-search-panel">
        <div className="panel-heading">
          <div>
            <h2>Browse knowledge</h2>
            <p className="muted small">
              Search all facts or filter goals by whether they are open or resolved.
            </p>
          </div>
          <span
            className="muted small"
            data-testid={resultBusy ? 'graph-results-busy' : 'graph-results-ready'}
            data-result-count={`${results.length} of ${resultTotal}`}
          >
            {resultBusy ? 'searching…' : `${results.length} of ${resultTotal}`}
          </span>
        </div>
        <form className="graph-search-form" onSubmit={submitSearch}>
          <label className="field field-search">
            <span>Search facts</span>
            <input
              data-testid="graph-search"
              type="search"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="goal, dashboard, decision…"
              autoComplete="off"
            />
          </label>
          <label className="field">
            <span>Node type</span>
            <select data-testid="graph-kind" value={kind} onChange={(event) => setKind(event.target.value)}>
              <option value="">All types</option>
              {nodeKinds.map((definition) => (
                <option key={definition.name} value={definition.name} title={definition.description}>
                  {definition.name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Repository</span>
            <select data-testid="graph-repo" value={repoId} onChange={(event) => setRepoId(event.target.value)}>
              <option value="">All repos</option>
              {repos.map((repo) => (
                <option key={repo.id} value={repo.id}>{repo.name}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Completion</span>
            <select data-testid="graph-scope" value={scope} onChange={(event) => setScope(event.target.value)}>
              <option value="all">All</option>
              <option value="active">Open / unresolved</option>
              <option value="resolved">Resolved / completed</option>
            </select>
          </label>
          <button className="button" data-testid="graph-search-submit" type="submit">
            Search
          </button>
          <label className="graph-filter-toggle">
            <input
              data-testid="graph-filter-toggle"
              type="checkbox"
              checked={showFilteredGraph}
              onChange={(event) => setShowFilteredGraph(event.target.checked)}
            />
            Filter graph to matches
          </label>
        </form>

        {resultError && <div className="error graph-search-error">{resultError}</div>}
        <div className="search-results" data-testid="graph-search-results">
          {!resultBusy && results.length === 0 && (
            <p className="muted">No matching facts. Try another search or filter.</p>
          )}
          {results.map((node) => (
            <button
              key={node.id}
              className={`search-result ${selectedResult?.id === node.id ? 'selected' : ''}`}
              data-testid={`graph-result-${node.id}`}
              onClick={() => selectResult(node)}
            >
              <span className="search-result-heading">
                <span className={`chip ${resolvedFor(node) ? 'chip-done' : 'chip-active'}`}>
                  {resolvedFor(node) ? 'resolved' : 'open'}
                </span>
                <strong>{node.name}</strong>
              </span>
              <span className="muted small">#{node.id} · {node.kind}</span>
              {node.description && <span className="search-result-description">{node.description}</span>}
              {goalDetails[node.id] && (
                <span className="muted small">
                  {goalDetails[node.id].resolvedTaskCount}/{goalDetails[node.id].taskCount} tasks · {goalDetails[node.id].completionSource}
                </span>
              )}
            </button>
          ))}
        </div>

        <details className="kind-catalog" data-testid="graph-kinds">
          <summary>Node types ({nodeKinds.length})</summary>
          <ul>
            {nodeKinds.map((definition) => (
              <li key={definition.name}>
                <span className="chip" style={{ color: kindColor(definition.name), borderColor: kindColor(definition.name) }}>
                  {definition.name}
                </span>
                <span className="muted small">{definition.description}</span>
              </li>
            ))}
          </ul>
        </details>
      </section>

      <div className="graph-layout">
        <section className="panel graph-panel">
          <div className="panel-heading">
            <div>
              <h2>Knowledge graph</h2>
              <p className="muted small">{nodeLabel}</p>
            </div>
            <div className="graph-actions">
              {filterActive && (
                <button className="button" onClick={() => networkRef.current?.fit({ animation: true })}>
                  Fit matches
                </button>
              )}
              {goalContext && goalTasks.length > 0 && (
                <button className="button" onClick={() => setGoalTaskGraph((active) => !active)}>
                  {goalTaskGraph ? 'Hide goal tasks' : 'Show goal tasks'}
                </button>
              )}
              {selectedNode && displayNodeIds.has(selectedNode.id) && (
                <button className="button" onClick={() => focusNode(selectedNode.id)}>
                  Focus selected
                </button>
              )}
              <button className="button" onClick={() => networkRef.current?.fit({ animation: true })}>
                Fit view
              </button>
            </div>
          </div>
          <div className="graph-canvas" data-testid="graph-canvas" ref={containerRef} />
          {graph && displayNodes.length === 0 && (
            <p className="muted graph-empty">
              {filterActive ? 'No matching nodes in the current graph snapshot.' : 'No persisted nodes yet.'}
            </p>
          )}
          {graphKinds.length > 0 && (
            <div className="legend">
              {graphKinds.map((graphKind) => (
                <span key={graphKind} className="legend-item">
                  <span className="legend-dot" style={{ background: kindColor(graphKind) }} />
                  {graphKind}
                </span>
              ))}
            </div>
          )}
        </section>

        <aside className="panel inspector">
          <h2>Node inspector</h2>
          {!selectedNode && <p className="muted">Select a result or a graph node to inspect it.</p>}
          {selectedNode && (
            <>
              <div className="inspector-heading">
                <strong>{selectedNode.name}</strong>
                <span className="chip" style={{ color: kindColor(selectedNode.kind), borderColor: kindColor(selectedNode.kind) }}>
                  {selectedNode.kind}
                </span>
              </div>
              <div className="stats">
                <div className="stat"><strong>#{selectedNode.id}</strong><span>node id</span></div>
                <div className="stat"><strong>{resolvedFor(selectedNode) ? 'resolved' : 'open'}</strong><span>state</span></div>
              </div>
              <p className="inspector-description">{selectedNode.description || 'No description recorded.'}</p>
              {goalContext && (
                <section className="goal-tasks">
                  <h3 className="inspector-subheading">
                    Tasks {goalTasks.filter((task) => task.resolved).length}/{goalTasks.length}
                  </h3>
                  {goalTasksLoading && <p className="muted small">Loading tasks…</p>}
                  {!goalTasksLoading && goalTasks.length === 0 && <p className="muted small">No tasks recorded.</p>}
                  <ul>
                    {goalTasks.map((task) => (
                      <li key={task.id}>
                        <button
                          className={`task-link ${selectedResult?.id === task.id ? 'selected' : ''}`}
                          onClick={() => selectResult(task)}
                        >
                          <span className={`chip ${task.resolved ? 'chip-done' : 'chip-active'}`}>
                            {task.resolved ? 'resolved' : 'open'}
                          </span>
                          <span>{task.name}</span>
                          <span className="muted small">#{task.id}</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                  {goalTasks.length > 0 && (
                    <button className="button" onClick={() => setGoalTaskGraph((active) => !active)}>
                      {goalTaskGraph ? 'Hide goal tasks in graph' : 'Show goal tasks in graph'}
                    </button>
                  )}
                </section>
              )}
              {displayNodeIds.has(selectedNode.id) && (
                <button className="button inspector-focus" onClick={() => focusNode(selectedNode.id)}>
                  Center in graph
                </button>
              )}
              <h3 className="inspector-subheading">Edges</h3>
              {graph && graph.edges.filter(
                (edge) => edge.source === selectedNode.id || edge.destination === selectedNode.id,
              ).length === 0 && <p className="muted small">No edges in this snapshot.</p>}
              <ul className="inspector-edges">
                {graph?.edges
                  .filter((edge) => edge.source === selectedNode.id || edge.destination === selectedNode.id)
                  .map((edge) => {
                    const outgoing = edge.source === selectedNode.id;
                    const otherId = outgoing ? edge.destination : edge.source;
                    const other = graph.nodes.find((node) => node.id === otherId);
                    return (
                      <li key={edge.id}>
                        <span className="mono small">{outgoing ? '→' : '←'} {edge.edgeType}</span>{' '}
                        <span className="muted small">#{otherId} {other?.name ?? ''}</span>
                      </li>
                    );
                  })}
              </ul>
            </>
          )}
        </aside>
      </div>
    </div>
  );
}
