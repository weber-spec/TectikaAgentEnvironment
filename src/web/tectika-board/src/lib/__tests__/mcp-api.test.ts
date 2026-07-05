// Verifies the MCP client methods build the right routes/methods/bodies.
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
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('connections client builds correct routes/methods/bodies', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.connections.catalog();
    await api.connections.list();
    await api.connections.create({ catalogId: 'anthropic', displayName: 'Claude', secrets: { token: 'sk-ant' } });
    await api.connections.validate('c1');
    await api.connections.remove('c1');
    await api.boardConnections.list('b1');
    await api.boardConnections.bind('b1', 'c1', { repo: 'x' });
    await api.boardConnections.unbind('b1', 'c1');

    assert.ok(calls[0].url.endsWith('/api/connections/catalog')); assert.equal(calls[0].method, 'GET');
    assert.ok(calls[1].url.endsWith('/api/connections')); assert.equal(calls[1].method, 'GET');
    assert.ok(calls[2].url.endsWith('/api/connections')); assert.equal(calls[2].method, 'POST');
    assert.match(calls[2].body!, /"catalogId":"anthropic"/);
    assert.match(calls[2].body!, /"token":"sk-ant"/);
    assert.ok(calls[3].url.endsWith('/api/connections/c1/validate')); assert.equal(calls[3].method, 'POST');
    assert.ok(calls[4].url.endsWith('/api/connections/c1')); assert.equal(calls[4].method, 'DELETE');
    assert.ok(calls[5].url.endsWith('/api/boards/b1/connections')); assert.equal(calls[5].method, 'GET');
    assert.ok(calls[6].url.endsWith('/api/boards/b1/connections/c1')); assert.equal(calls[6].method, 'PUT');
    assert.match(calls[6].body!, /"config"/);
    assert.ok(calls[7].url.endsWith('/api/boards/b1/connections/c1')); assert.equal(calls[7].method, 'DELETE');
  } finally { restore(); }
});
