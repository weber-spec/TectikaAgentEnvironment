// Verifies the api.comments client builds the correct routes / methods / bodies.
// Run: node --test --experimental-transform-types src/lib/__tests__/comments-api.test.ts
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
    return new Response(JSON.stringify({ id: 'c1' }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('api.comments.list issues GET to /api/boards/{boardId}/tasks/{taskId}/comments', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.comments.list('b1', 't1');
    assert.ok(calls[0].url.endsWith('/api/boards/b1/tasks/t1/comments'), `url: ${calls[0].url}`);
    assert.equal(calls[0].method, 'GET');
  } finally { restore(); }
});

test('api.comments.create issues POST with correct body', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.comments.create('b1', 't1', { kind: 'note', noteType: 'decision', body: 'x', mentions: ['maya@tectika.com'] });
    assert.ok(calls[0].url.endsWith('/api/boards/b1/tasks/t1/comments'), `url: ${calls[0].url}`);
    assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].body!, /"kind":"note"/);
    assert.match(calls[0].body!, /"noteType":"decision"/);
    assert.match(calls[0].body!, /"body":"x"/);
    assert.match(calls[0].body!, /"mentions":\["maya@tectika.com"\]/);
  } finally { restore(); }
});

test('api.comments.share issues POST to /comments/{id}/share with {shared}', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.comments.share('b1', 't1', 'c1', true);
    assert.ok(calls[0].url.endsWith('/api/boards/b1/tasks/t1/comments/c1/share'), `url: ${calls[0].url}`);
    assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].body!, /"shared":true/);
  } finally { restore(); }
});
