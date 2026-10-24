import { useCallback, useEffect, useRef, useState } from 'react';
import { Network, type Edge, type Node, type Options } from 'vis-network/standalone';
import { api } from './api';
import type { GraphNode, GraphSnapshot } from './types';

/** Mirrors the knowledge snapshot defaults (Anima.Knowledge.GraphSnapshotQuery). */
const maxNodes = 500;
const maxEdges = 1000;

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
  const [error, setError] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const networkRef = useRef<Network | null>(null);
  const requestRef = useRef(0);

  const load = useCallback(async () => {
    const request = ++requestRef.current;
    try {
      const snapshot = await api.graph(maxNodes, maxEdges);
      if (request !== requestRef.current) return;
      setGraph(snapshot);
      setError(null);
    } catch (exception) {
      if (request === requestRef.current) {
        setError(String(exception));
      }
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, revision]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const network = new Network(container, { nodes: [], edges: [] }, networkOptions);
    networkRef.current = network;
    network.on('selectNode', (params) => {
      const picked = params?.nodes?.[0];
      if (picked !== undefined) {
        setSelectedId(Number(picked));
      }
    });
    network.on('deselectNode', () => setSelectedId(null));

    return () => {
      network.destroy();
      networkRef.current = null;
    };
  }, []);

  // Replace the data set on refresh instead of recreating the network, so pan
  // and zoom survive live updates.
  useEffect(() => {
    if (!graph || !networkRef.current) return;

    const nodes: Node[] = graph.nodes.map((node) => {
      const color = kindColor(node.kind);
      return {
        id: node.id,
        label: node.name,
        title: `${node.kind} · #${node.id}\n${node.description}`,
        color: { background: color, border: color, highlight: { background: color, border: '#ffffff' } },
        borderWidth: node.resolved ? 1 : 3,
      };
    });

    const edges: Edge[] = graph.edges.map((edge) => ({
      id: edge.id,
      from: edge.source,
      to: edge.destination,
      title: edge.edgeType,
    }));

    networkRef.current.setData({ nodes, edges });
  }, [graph]);

  const selectedNode: GraphNode | null =
    selectedId === null ? null : graph?.nodes.find((node) => node.id === selectedId) ?? null;

  const kinds = graph
    ? [...new Set(graph.nodes.map((node) => node.kind))].sort((left, right) => left.localeCompare(right))
    : [];

  const nodeLabel = graph
    ? `showing ${graph.nodes.length} of ${graph.nodeCount} nodes · ${graph.edges.length} of ${graph.edgeCount} edges`
    : 'loading graph';

  return (
    <div className="graph-tab">
      {error && <div className="error">{error}</div>}

      {graph?.truncated && (
        <div className="banner">
          <strong>Graph truncated.</strong> The snapshot is a prefix of the graph: caps are {maxNodes} nodes and{' '}
          {maxEdges} edges, and edges are only included when both endpoints are. Showing {graph.nodes.length}/{graph.nodeCount}{' '}
          nodes and {graph.edges.length}/{graph.edgeCount} edges.
        </div>
      )}

      <div className="graph-layout">
        <section className="panel graph-panel">
          <div className="panel-heading">
            <div>
              <h2>Knowledge graph</h2>
              <p className="muted small">{nodeLabel}</p>
            </div>
            <div className="graph-actions">
              <button className="button" onClick={() => networkRef.current?.fit({ animation: true })}>
                Fit view
              </button>
            </div>
          </div>
          <div className="graph-canvas" ref={containerRef} />
          {graph && graph.nodes.length === 0 && (
            <p className="muted graph-empty">No persisted nodes yet.</p>
          )}
          {kinds.length > 0 && (
            <div className="legend">
              {kinds.map((kind) => (
                <span key={kind} className="legend-item">
                  <span className="legend-dot" style={{ background: kindColor(kind) }} />
                  {kind}
                </span>
              ))}
            </div>
          )}
        </section>

        <aside className="panel inspector">
          <h2>Node inspector</h2>
          {!selectedNode && <p className="muted">Select a node to inspect its summary.</p>}
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
                <div className="stat"><strong>{selectedNode.resolved ? 'resolved' : 'open'}</strong><span>state</span></div>
              </div>
              <p className="inspector-description">{selectedNode.description || 'No description recorded.'}</p>
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
