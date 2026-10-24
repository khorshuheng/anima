import { useEffect, useState } from 'react';
import { api } from './api';
import type { BusStreamEvent } from './types';

export type StreamStatus = 'connecting' | 'live' | 'offline';

const feedLimit = 200;

/**
 * Single SSE subscription shared by the tabs. While the stream is down the
 * connection is retried by EventSource itself; callers poll instead of relying
 * on events until it reopens.
 */
export function useBusStream(topics: string): {
  status: StreamStatus;
  events: BusStreamEvent[];
} {
  const [status, setStatus] = useState<StreamStatus>('connecting');
  const [events, setEvents] = useState<BusStreamEvent[]>([]);

  useEffect(() => {
    setStatus('connecting');
    const source = new EventSource(api.busStreamUrl(topics));

    source.onopen = () => setStatus('live');
    source.onerror = () => setStatus('offline');

    const onMessage = (event: MessageEvent<string>) => {
      try {
        const parsed = JSON.parse(event.data) as BusStreamEvent;
        if (!parsed.topic || !parsed.typeName) return;
        setEvents((current) => [parsed, ...current].slice(0, feedLimit));
      } catch {
        // Ignore malformed frames; the polling fallback still refreshes state.
      }
    };

    source.addEventListener('message', onMessage);
    return () => {
      source.removeEventListener('message', onMessage);
      source.close();
    };
  }, [topics]);

  return { status, events };
}
