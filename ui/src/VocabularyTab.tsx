import { useCallback, useEffect, useState } from 'react';
import { api } from './api';
import type { EdgeType, NodeKind, Vocabulary } from './types';

interface VocabularyTabProps {
  /** Incremented by the shell on live events and polling ticks. */
  revision: number;
}

function packLabel(source: string): string {
  return source === 'core' ? 'core' : source;
}

function pairLabel(pair: string[]): string {
  return `${pair[0]} → ${pair[1]}`;
}

function flags(edge: EdgeType): string[] {
  const result: string[] = [];
  if (edge.resolves) result.push('resolves');
  if (edge.single_outgoing) result.push('single outgoing');
  if (edge.symmetric) result.push('symmetric');
  if (edge.acyclic) result.push('acyclic');
  if (edge.deprecated) result.push('deprecated');
  return result;
}

/** Read-only view of the vocabulary the knowledge service actually loaded. */
export function VocabularyTab({ revision }: VocabularyTabProps) {
  const [vocabulary, setVocabulary] = useState<Vocabulary | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setVocabulary(await api.vocabulary());
      setError(null);
    } catch (exception) {
      setError(String(exception));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, revision]);

  if (error) {
    return (
      <section className="panel">
        <h2 className="panel-heading">Vocabulary</h2>
        <p className="error">{error}</p>
      </section>
    );
  }

  if (!vocabulary) {
    return (
      <section className="panel">
        <h2 className="panel-heading">Vocabulary</h2>
        <p className="small">Loading vocabulary…</p>
      </section>
    );
  }

  const packs = Array.from(
    new Set([
      ...vocabulary.node_kinds.map((kind) => packLabel(kind.source)),
      ...vocabulary.edge_types.map((edge) => packLabel(edge.source)),
    ]),
  ).sort();
  const packKinds = vocabulary.node_kinds.filter((kind) => kind.source !== 'core').length;
  const packEdges = vocabulary.edge_types.filter((edge) => edge.source !== 'core').length;

  return (
    <div className="vocabulary-tab">
      <section className="panel">
        <h2 className="panel-heading">Vocabulary</h2>
        <p className="small">
          {vocabulary.node_kinds.length} node kinds and {vocabulary.edge_types.length} edge types,
          of which {packKinds} kinds and {packEdges} edge types come from user packs.
        </p>
        <div className="stats">
          <div className="stat">
            <strong>{packs.length}</strong>
            <span>{packs.join(', ')}</span>
          </div>
          <div className="stat">
            <strong>{vocabulary.traits.length}</strong>
            <span>{vocabulary.traits.join(', ') || '—'}</span>
          </div>
        </div>
      </section>

      <section className="panel">
        <h2 className="panel-heading">Node kinds</h2>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Pack</th>
                <th>Traits</th>
                <th>Required props</th>
                <th>Status</th>
                <th>Description</th>
              </tr>
            </thead>
            <tbody>
              {vocabulary.node_kinds.map((kind: NodeKind) => (
                <tr key={kind.id} className={kind.source === 'core' ? undefined : 'vocabulary-pack-row'}>
                  <td>
                    <code>{kind.name}</code>
                  </td>
                  <td>{packLabel(kind.source)}</td>
                  <td>{kind.traits.join(', ') || '—'}</td>
                  <td>{kind.required_props.join(', ') || '—'}</td>
                  <td>
                    <span className={kind.deprecated ? 'chip chip-warn' : 'chip chip-done'}>
                      {kind.deprecated ? 'deprecated' : 'active'}
                    </span>
                  </td>
                  <td className="small">{kind.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="panel">
        <h2 className="panel-heading">Edge types</h2>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Pack</th>
                <th>Pairs</th>
                <th>Inverse</th>
                <th>Traits</th>
                <th>Flags</th>
                <th>Description</th>
              </tr>
            </thead>
            <tbody>
              {vocabulary.edge_types.map((edge) => (
                <tr key={edge.id} className={edge.source === 'core' ? undefined : 'vocabulary-pack-row'}>
                  <td>
                    <code>{edge.name}</code>
                  </td>
                  <td>{packLabel(edge.source)}</td>
                  <td>{edge.pairs.map(pairLabel).join(', ') || 'any'}</td>
                  <td>{edge.inverse_name ?? '—'}</td>
                  <td>{edge.traits.join(', ') || '—'}</td>
                  <td>{flags(edge).join(', ') || '—'}</td>
                  <td className="small">{edge.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
