import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from './api';
import type { BusSnapshot, BusStreamEvent } from './types';
import type { StreamStatus } from './useBusStream';

const snapshotRefreshDelay = 1_500;

function formatTime(value: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleTimeString();
}

interface BusTabProps {
  events: BusStreamEvent[];
  status: StreamStatus;
  /** Incremented by the shell on live events and polling ticks. */
  revision: number;
}

export function BusTab({ events, status, revision }: BusTabProps) {
  const [snapshot, setSnapshot] = useState<BusSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const requestRef = useRef(0);
  const latestEvent = events[0];

  const load = useCallback(async () => {
    const request = ++requestRef.current;
    try {
      const next = await api.bus();
      if (request !== requestRef.current) return;
      setSnapshot(next);
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

  // Keep counters moving while the stream delivers traffic without refetching
  // once per frame: a burst of events collapses into one trailing refresh.
  useEffect(() => {
    if (!latestEvent) return;
    const timer = window.setTimeout(() => void load(), snapshotRefreshDelay);
    return () => window.clearTimeout(timer);
  }, [latestEvent, load]);

  return (
    <div className="bus-tab">
      {error && <div className="error">{error}</div>}

      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>Bus counters</h2>
            <p className="muted small">
              In-process since the bus host started ·{' '}
              {status === 'live' ? 'counters refresh from the live stream' : 'counters refresh by polling'}
            </p>
          </div>
        </div>
        {!snapshot && <p className="muted">Loading bus snapshot…</p>}
        {snapshot && (
          <div className="stats">
            <div className="stat"><strong>{snapshot.totalPublished}</strong><span>published</span></div>
            <div className="stat"><strong>{snapshot.totalDelivered}</strong><span>delivered</span></div>
            <div className="stat"><strong>{snapshot.totalDropped}</strong><span>dropped</span></div>
            <div className="stat"><strong>{snapshot.totalFailed}</strong><span>failed</span></div>
            <div className="stat"><strong>{snapshot.topics.length}</strong><span>topics</span></div>
            <div className="stat"><strong>{snapshot.subscriptions.length}</strong><span>subscribers</span></div>
          </div>
        )}
      </section>

      <section className="panel">
        <h2>Topics and producers</h2>
        {snapshot && snapshot.topics.length === 0 && <p className="muted">No bus traffic recorded yet.</p>}
        {snapshot && snapshot.topics.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Topic</th>
                  <th>Published</th>
                  <th>Last message</th>
                  <th>Type names</th>
                  <th>Senders</th>
                </tr>
              </thead>
              <tbody>
                {snapshot.topics.map((topic) => (
                  <tr key={topic.topic}>
                    <td className="mono">{topic.topic}</td>
                    <td>{topic.publishedCount}</td>
                    <td className="muted small">{formatTime(topic.lastTimestamp)}</td>
                    <td>
                      {topic.typeNames.length === 0 && <span className="muted small">—</span>}
                      {topic.typeNames.map((typeName) => <span key={typeName} className="chip">{typeName}</span>)}
                    </td>
                    <td>
                      {topic.senders.length === 0 && <span className="muted small">—</span>}
                      {topic.senders.map((sender) => <span key={sender} className="chip">{sender}</span>)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="panel">
        <h2>Subscribers</h2>
        {snapshot && snapshot.subscriptions.length === 0 && <p className="muted">No subscriptions registered.</p>}
        {snapshot && snapshot.subscriptions.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Pattern</th>
                  <th>Delivered</th>
                  <th>Dropped</th>
                  <th>Failed</th>
                  <th>Last activity</th>
                </tr>
              </thead>
              <tbody>
                {snapshot.subscriptions.map((subscription) => (
                  <tr key={subscription.id}>
                    <td>
                      <strong>{subscription.name}</strong>
                      <div className="mono small muted">{subscription.id}</div>
                    </td>
                    <td className="mono">{subscription.pattern}</td>
                    <td>{subscription.delivered}</td>
                    <td className={subscription.dropped > 0 ? 'count-warn' : undefined}>{subscription.dropped}</td>
                    <td className={subscription.failed > 0 ? 'count-danger' : undefined}>{subscription.failed}</td>
                    <td className="muted small">{formatTime(subscription.lastActivity)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>Live events</h2>
            <p className="muted small">
              anima.** · last {events.length} buffered ·{' '}
              {status === 'live' ? 'streaming' : status === 'offline' ? 'stream offline, polling' : 'connecting'}
            </p>
          </div>
        </div>
        {events.length === 0 && <p className="muted">No live events yet.</p>}
        {events.length > 0 && (
          <ol className="feed">
            {events.map((event, index) => (
              <li key={`${event.messageId}-${index}`} className="feed-item">
                <span className="mono small muted">{formatTime(event.timestamp)}</span>
                <span className="mono small">{event.topic}</span>
                <span className="chip">{event.typeName}</span>
                <span className="small muted">{event.sender || 'unknown sender'}</span>
              </li>
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}
