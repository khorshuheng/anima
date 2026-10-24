import { useCallback, useEffect, useState } from 'react';
import { BusTab } from './BusTab';
import { GraphTab } from './GraphTab';
import { WorkflowsTab } from './WorkflowsTab';
import { useBusStream } from './useBusStream';
import type { BusStreamEvent } from './types';

type Tab = 'workflows' | 'graph' | 'bus';

const tabs: Array<{ id: Tab; label: string }> = [
  { id: 'workflows', label: 'Workflows' },
  { id: 'graph', label: 'Graph' },
  { id: 'bus', label: 'Bus' },
];

const refreshDelay = 300;
const pollInterval = 10_000;

/** Workflow and graph state only changes when the harness posts one of these. */
function isDataEvent(event: BusStreamEvent): boolean {
  return event.typeName.includes('WorkflowEvent') || event.typeName.includes('GraphEvent');
}

export default function App() {
  const [tab, setTab] = useState<Tab>('workflows');
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<number | null>(null);
  const [revision, setRevision] = useState(0);
  const { status, events } = useBusStream('anima.**');
  const latestEvent = events[0];

  const refresh = useCallback(() => setRevision((current) => current + 1), []);

  useEffect(() => {
    if (!latestEvent || !isDataEvent(latestEvent)) return;
    const timer = window.setTimeout(refresh, refreshDelay);
    return () => window.clearTimeout(timer);
  }, [latestEvent, refresh]);

  // The stream reconnects on its own; poll only while it is unavailable.
  useEffect(() => {
    if (status !== 'offline') return;
    const timer = window.setInterval(refresh, pollInterval);
    return () => window.clearInterval(timer);
  }, [status, refresh]);

  const statusLabel = status === 'live' ? 'live' : status === 'offline' ? 'polling' : 'connecting';

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>Anima</h1>
          <p>Workflows, knowledge graph and bus traffic</p>
        </div>
        <nav className="tabs">
          {tabs.map((item) => (
            <button
              key={item.id}
              className={`tab ${tab === item.id ? 'active' : ''}`}
              onClick={() => setTab(item.id)}
            >
              {item.label}
            </button>
          ))}
        </nav>
        <div className="topbar-actions">
          <span className={`live live-${status}`}>{statusLabel}</span>
          <button className="button" onClick={refresh}>
            Refresh
          </button>
        </div>
      </header>

      {tab === 'workflows' && (
        <WorkflowsTab
          selectedId={selectedWorkflowId}
          onSelect={setSelectedWorkflowId}
          revision={revision}
        />
      )}
      {tab === 'graph' && <GraphTab revision={revision} />}
      {tab === 'bus' && <BusTab events={events} status={status} revision={revision} />}
    </div>
  );
}
