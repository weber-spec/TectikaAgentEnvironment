// Verifies the board reset/clone/workspace client methods build the right routes/methods/bodies.
// Run: node --test --experimental-transform-types src/lib/__tests__/board-settings-api.test.ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as nodeModule from 'node:module';

type ResolveCtx = Record<string, unknown>;
type ResolveResult = { url: string; format?: string | null };
type NextResolve = (specifier: string, context: ResolveCtx) => ResolveResult;
const registerHooks = (nodeModule as unknown as {
  registerHooks: (hooks: { resolve: (s: string, c: ResolveCtx, n: NextResolve) => ResolveResult }) => void;
}).registerHooks;
registerHooks({
  resolve(specifier: string, context: ResolveCtx, nextResolve: NextResolve): ResolveResult {
    if (/^\.\.?\//.test(specifier) && !/\.[a-z]+$/i.test(specifier)) {
      try { return nextResolve(specifier + '.ts', context); } catch { return nextResolve(specifier, context); }
    }
    return nextResolve(specifier, context);
  },
});

const { api } = await import('../api.ts');

interface Call { url: string; method: string; body?: string }
function stubFetch(): { calls: Call[]; restore: () => void } {
  const calls: Call[] = [];
  const orig = globalThis.fetch;
  globalThis.fetch = (async (url: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string | undefined });
    return new Response(JSON.stringify({ id: 'b2' }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('board settings client builds correct routes/methods/bodies', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.boards.reset('b1', true);
    await api.boards.clone('b1', { name: 'Copy', includeData: false });
    await api.boards.workspace.get('b1');
    await api.boards.workspace.start('b1');
    await api.boards.workspace.restart('b1');
    await api.boards.workspace.terminate('b1');

    assert.ok(calls[0].url.endsWith('/api/boards/b1/reset')); assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].body!, /"clearRepo":true/);
    assert.ok(calls[1].url.endsWith('/api/boards/b1/clone')); assert.equal(calls[1].method, 'POST');
    assert.match(calls[1].body!, /"includeData":false/);
    assert.ok(calls[2].url.endsWith('/api/boards/b1/workspace')); assert.equal(calls[2].method, 'GET');
    assert.ok(calls[3].url.endsWith('/api/boards/b1/workspace')); assert.equal(calls[3].method, 'POST');
    assert.ok(calls[4].url.endsWith('/api/boards/b1/workspace/restart')); assert.equal(calls[4].method, 'POST');
    assert.ok(calls[5].url.endsWith('/api/boards/b1/workspace')); assert.equal(calls[5].method, 'DELETE');
  } finally { restore(); }
});
