// Pins the connection budget: no matter how many components want run events for a board, the page holds
// ONE EventSource for it.
//
// Why this matters: the board used to open an EventSource per task-with-a-run, plus more from the item
// panel. Browsers allow ~6 concurrent connections per origin on HTTP/1.1, so a board with a handful of
// finished runs exhausted the pool and every later fetch() hung forever — which is what made "Reset & run"
// silently do nothing and left the board on "Loading…". The regression to guard against is a component
// going back to opening its own stream.
//
// Like task-actions.test.ts, board-stream imports './api' with an extensionless specifier; register a
// resolve hook so Node's ESM loader appends '.ts'.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as nodeModule from 'node:module';

type ResolveCtx = Record<string, unknown>;
type ResolveResult = { url: string; format?: string | null };
type NextResolve = (specifier: string, context: ResolveCtx) => ResolveResult;
const registerHooks = (nodeModule as unknown as {
  registerHooks: (hooks: {
    resolve: (specifier: string, context: ResolveCtx, nextResolve: NextResolve) => ResolveResult;
  }) => void;
}).registerHooks;

registerHooks({
  resolve(specifier: string, context: ResolveCtx, nextResolve: NextResolve): ResolveResult {
    if (/^\.\.?\//.test(specifier) && !/\.[a-z]+$/i.test(specifier)) {
      try {
        return nextResolve(specifier + '.ts', context);
      } catch {
        return nextResolve(specifier, context);
      }
    }
    return nextResolve(specifier, context);
  },
});

// ── a fake EventSource ────────────────────────────────────────────────────────
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  readyState = FakeEventSource.CONNECTING;
  closed = false;
  onopen: (() => void) | null = null;
  onmessage: ((e: { data: string }) => void) | null = null;
  onerror: (() => void) | null = null;

  readonly url: string;

  // Plain field assignment, not a TS parameter property: Node's --experimental-strip-types can't
  // transform those (same reason ApiError in api.ts avoids them).
  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  close() {
    this.closed = true;
    this.readyState = FakeEventSource.CLOSED;
  }

  // test drivers
  emit(payload: unknown) { this.onmessage?.({ data: JSON.stringify(payload) }); }
  emitRaw(data: string) { this.onmessage?.({ data }); }
}

(globalThis as unknown as { EventSource: unknown }).EventSource = FakeEventSource;

const { subscribeBoardEvents, openBoardStreamCount, __setGraceMs, __resetBoardStreams } =
  await import('../board-stream.ts');

const GRACE = 20;
const afterGrace = () => new Promise(r => setTimeout(r, GRACE * 3));

function reset() {
  __resetBoardStreams();
  __setGraceMs(GRACE);
  FakeEventSource.instances = [];
}

const live = () => FakeEventSource.instances.filter(es => !es.closed);

test('two subscribers on one board share a single EventSource', async () => {
  reset();
  const a: unknown[] = [], b: unknown[] = [];
  const offA = subscribeBoardEvents('b1', e => a.push(e));
  const offB = subscribeBoardEvents('b1', e => b.push(e));

  assert.equal(FakeEventSource.instances.length, 1, 'expected exactly one EventSource for the board');
  assert.equal(openBoardStreamCount(), 1);

  FakeEventSource.instances[0].emit({ type: 'run_event', runId: 'r1', taskId: 't1' });
  assert.equal(a.length, 1);
  assert.equal(b.length, 1, 'both subscribers get the event off the one connection');

  offA(); offB();
  await afterGrace();
});

test('unsubscribing one subscriber leaves the stream open for the other', async () => {
  reset();
  const offA = subscribeBoardEvents('b1', () => {});
  const seen: unknown[] = [];
  const offB = subscribeBoardEvents('b1', e => seen.push(e));

  offA();
  await afterGrace();

  assert.equal(live().length, 1, 'stream must stay open while a subscriber remains');
  FakeEventSource.instances[0].emit({ type: 'run_event', runId: 'r1' });
  assert.equal(seen.length, 1);

  offB();
  await afterGrace();
});

test('the last unsubscribe closes the stream — after the grace window, not before', async () => {
  reset();
  const off = subscribeBoardEvents('b1', () => {});
  off();

  assert.equal(live().length, 1, 'must not close synchronously');
  await afterGrace();
  assert.equal(live().length, 0);
  assert.equal(openBoardStreamCount(), 0);
});

test('re-subscribing inside the grace window reuses the connection (React StrictMode remount)', async () => {
  reset();
  // StrictMode mounts, unmounts and remounts every effect in dev. Without the grace window this tears the
  // connection down and builds a new one on every mount.
  const off1 = subscribeBoardEvents('b1', () => {});
  off1();

  const seen: unknown[] = [];
  const off2 = subscribeBoardEvents('b1', e => seen.push(e));
  await afterGrace();

  assert.equal(FakeEventSource.instances.length, 1, 'no second EventSource was constructed');
  assert.equal(live().length, 1, 'the pending close was cancelled');

  FakeEventSource.instances[0].emit({ type: 'run_event', runId: 'r1' });
  assert.equal(seen.length, 1, 'the reused stream still delivers');

  off2();
  await afterGrace();
});

test('different boards get their own streams', async () => {
  reset();
  const off1 = subscribeBoardEvents('b1', () => {});
  const off2 = subscribeBoardEvents('b2', () => {});

  assert.equal(FakeEventSource.instances.length, 2);
  assert.ok(FakeEventSource.instances[0].url.includes('/api/boards/b1/stream'));
  assert.ok(FakeEventSource.instances[1].url.includes('/api/boards/b2/stream'));

  off1(); off2();
  await afterGrace();
});

test('a throwing handler does not starve the others', async () => {
  reset();
  const seen: unknown[] = [];
  const off1 = subscribeBoardEvents('b1', () => { throw new Error('boom'); });
  const off2 = subscribeBoardEvents('b1', e => seen.push(e));

  FakeEventSource.instances[0].emit({ type: 'run_event', runId: 'r1' });
  assert.equal(seen.length, 1);

  off1(); off2();
  await afterGrace();
});

test('a malformed frame is skipped and the stream stays open', async () => {
  reset();
  const seen: unknown[] = [];
  const off = subscribeBoardEvents('b1', e => seen.push(e));

  FakeEventSource.instances[0].emitRaw('not json');
  FakeEventSource.instances[0].emit({ type: 'run_event', runId: 'r1' });

  assert.equal(seen.length, 1);
  assert.equal(live().length, 1);

  off();
  await afterGrace();
});

test('a transport blip is left to the browser — we never close a CONNECTING stream', async () => {
  reset();
  const off = subscribeBoardEvents('b1', () => {});
  const es = FakeEventSource.instances[0];

  es.readyState = FakeEventSource.CONNECTING;   // browser is already retrying
  es.onerror?.();

  assert.equal(es.closed, false, 'closing here would kill the browser\'s own reconnect permanently');
  assert.equal(FakeEventSource.instances.length, 1, 'and we must not stack a second connection on top');

  off();
  await afterGrace();
});
