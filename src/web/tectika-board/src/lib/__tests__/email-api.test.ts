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
    return new Response(JSON.stringify({ id: 'd1', name: 'acme.com', status: 'not_started' }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('email client builds correct routes/methods/bodies', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.email.domains('b1');
    await api.email.addDomain('b1', 'acme.com');
    await api.email.getDomain('b1', 'd1');
    await api.email.verifyDomain('b1', 'd1');
    await api.email.deleteDomain('b1', 'd1');
    await api.email.setFrom('b1', 'a@acme.com');

    assert.ok(calls[0].url.endsWith('/api/boards/b1/email/domains')); assert.equal(calls[0].method, 'GET');
    assert.ok(calls[1].url.endsWith('/api/boards/b1/email/domains')); assert.equal(calls[1].method, 'POST');
    assert.match(calls[1].body!, /"name":"acme.com"/);
    assert.ok(calls[2].url.endsWith('/api/boards/b1/email/domains/d1')); assert.equal(calls[2].method, 'GET');
    assert.ok(calls[3].url.endsWith('/api/boards/b1/email/domains/d1/verify')); assert.equal(calls[3].method, 'POST');
    assert.ok(calls[4].url.endsWith('/api/boards/b1/email/domains/d1')); assert.equal(calls[4].method, 'DELETE');
    assert.ok(calls[5].url.endsWith('/api/boards/b1/email/from')); assert.equal(calls[5].method, 'PUT');
    assert.match(calls[5].body!, /"from":"a@acme.com"/);
  } finally { restore(); }
});
