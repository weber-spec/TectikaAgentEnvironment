// One SSE connection per board, shared by every subscriber on the page.
//
// The board page used to open an EventSource per task-with-a-run (plus more from the item panel).
// Browsers cap concurrent connections per origin at ~6 on HTTP/1.1, so a board with a handful of
// finished runs exhausted the pool and every subsequent fetch() hung forever — no error, just a
// pending request. That's why "Reset & run" silently did nothing and the board sat on "Loading…".
//
// Connection count is now O(1) in the number of tasks: board-context, the item panel's run trace and
// the CLI bridge all subscribe here and share one EventSource, refcounted.

import { api } from './api';
import type { AgentEvent } from './types';

export type StreamStatus = 'connecting' | 'open' | 'error';

type Handler = (ev: AgentEvent) => void;
type StatusHandler = (s: StreamStatus) => void;

interface Entry {
  es: EventSource;
  handlers: Set<Handler>;
  statusHandlers: Set<StatusHandler>;
  status: StreamStatus;
  closeTimer?: ReturnType<typeof setTimeout>;
  retryTimer?: ReturnType<typeof setTimeout>;
  failures: number;
}

const streams = new Map<string, Entry>();

// React StrictMode double-invokes every effect in dev, so the last unsubscribe is routinely followed by
// an immediate re-subscribe. Closing on a short delay makes that a no-op instead of a connection churn.
let graceMs = 400;

const RETRY_BASE_MS = 1_000;
const RETRY_MAX_MS = 30_000;

function url(boardId: string) {
  return `${api.base}/api/boards/${boardId}/stream`;
}

function setStatus(entry: Entry, status: StreamStatus) {
  entry.status = status;
  for (const h of entry.statusHandlers) {
    try { h(status); } catch { /* a bad status listener must not take the stream down */ }
  }
}

function connect(boardId: string, entry: Entry): EventSource {
  const es = new EventSource(url(boardId));

  es.onopen = () => {
    entry.failures = 0;
    setStatus(entry, 'open');
  };

  es.onmessage = (e) => {
    let ev: AgentEvent;
    try { ev = JSON.parse(e.data as string) as AgentEvent; } catch { return; }   // skip malformed
    for (const h of entry.handlers) {
      try { h(ev); } catch { /* one throwing subscriber must not starve the others */ }
    }
  };

  es.onerror = () => {
    // Do NOT close here. On a transport blip the browser reconnects on its own (readyState CONNECTING),
    // and calling close() in that state kills the stream permanently. Only a CLOSED socket — a non-2xx
    // response, a CORS failure — is ours to retry.
    setStatus(entry, 'error');
    if (es.readyState !== EventSource.CLOSED) return;
    if (entry.handlers.size === 0) return;      // nobody left to serve
    if (entry.retryTimer) return;               // already scheduled

    const backoff = Math.min(RETRY_BASE_MS * 2 ** entry.failures, RETRY_MAX_MS);
    const jittered = backoff * (0.5 + Math.random() / 2);
    entry.failures++;
    entry.retryTimer = setTimeout(() => {
      entry.retryTimer = undefined;
      if (entry.handlers.size === 0 || streams.get(boardId) !== entry) return;
      entry.es.close();
      entry.es = connect(boardId, entry);
    }, jittered);
  };

  setStatus(entry, 'connecting');
  return es;
}

function teardown(boardId: string, entry: Entry) {
  if (entry.closeTimer) clearTimeout(entry.closeTimer);
  if (entry.retryTimer) clearTimeout(entry.retryTimer);
  entry.es.close();
  streams.delete(boardId);
}

/**
 * Subscribe to every run event on a board. Returns an unsubscribe function.
 * The underlying EventSource is shared with any other subscriber on the same board.
 */
export function subscribeBoardEvents(
  boardId: string,
  onEvent: Handler,
  onStatus?: StatusHandler,
): () => void {
  let entry = streams.get(boardId);

  if (entry) {
    // Reuse — and cancel a pending close, so a StrictMode remount never churns the connection.
    if (entry.closeTimer) {
      clearTimeout(entry.closeTimer);
      entry.closeTimer = undefined;
    }
  } else {
    entry = { es: undefined as unknown as EventSource, handlers: new Set(), statusHandlers: new Set(), status: 'connecting', failures: 0 };
    entry.es = connect(boardId, entry);
    streams.set(boardId, entry);

    if (process.env.NODE_ENV !== 'production' && streams.size > 2) {
      console.warn(
        `[board-stream] ${streams.size} board streams open — one per board is expected. ` +
        `Opening a stream per task is what exhausted the browser's connection pool.`,
      );
    }
  }

  const current = entry;
  current.handlers.add(onEvent);
  if (onStatus) {
    current.statusHandlers.add(onStatus);
    onStatus(current.status);
  }

  return () => {
    current.handlers.delete(onEvent);
    if (onStatus) current.statusHandlers.delete(onStatus);
    if (current.handlers.size > 0) return;
    if (streams.get(boardId) !== current) return;

    current.closeTimer = setTimeout(() => {
      // Re-check: a subscriber may have arrived during the grace window.
      const live = streams.get(boardId);
      if (live === current && current.handlers.size === 0) teardown(boardId, current);
    }, graceMs);
  };
}

/** Open board streams. Used by the dev warning above and by tests. */
export function openBoardStreamCount(): number {
  return streams.size;
}

// ── test hooks ────────────────────────────────────────────────────────────────
export function __setGraceMs(ms: number) { graceMs = ms; }
export function __resetBoardStreams() {
  for (const [boardId, entry] of [...streams]) teardown(boardId, entry);
  graceMs = 400;
}
