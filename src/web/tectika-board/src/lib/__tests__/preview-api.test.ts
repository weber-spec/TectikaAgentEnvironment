// Tests the api.preview client builds the correct routes / methods / body, and that
// `get` tolerates a 404 (no active preview) by resolving to null.
//
// api.ts uses extensionless relative imports (`./types`, `./telemetry`) — the Next/tsc
// bundler resolver handles those, but Node's ESM loader needs explicit extensions. We
// register a synchronous resolve hook (Node 22 `module.registerHooks`) to append `.ts`
// to extensionless relative specifiers so the real client can be imported unchanged.
// Run with: node --test --experimental-transform-types src/lib/__tests__/preview-api.test.ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as nodeModule from 'node:module';

type ResolveCtx = Record<string, unknown>;
type ResolveResult = { url: string; format?: string | null };
type NextResolve = (specifier: string, context: ResolveCtx) => ResolveResult;
// `registerHooks` is a Node 22 API; the repo's @types/node (v20) predates it.
const registerHooks = (nodeModule as unknown as {
  registerHooks: (hooks: {
    resolve: (specifier: string, context: ResolveCtx, nextResolve: NextResolve) => ResolveResult;
  }) => void;
}).registerHooks;

registerHooks({
  resolve(specifier: string, context: ResolveCtx, nextResolve: NextResolve): ResolveResult {
    // Only rewrite extensionless relative imports (e.g. './telemetry', './types').
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

const { api } = await import('../api.ts');

interface Call { url: string; method: string; body?: string }

function stubFetch(status = 200): { calls: Call[]; restore: () => void } {
  const calls: Call[] = [];
  const orig = globalThis.fetch;
  globalThis.fetch = (async (url: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string | undefined });
    const payload = JSON.stringify({
      id: 'tpv-x', boardId: 'b1', branch: 'main', status: 'Provisioning',
      createdAt: '2026-06-22T00:00:00Z', expiresAt: '2026-06-22T01:00:00Z',
    });
    return new Response(status === 200 ? payload : '', {
      status,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('preview client builds correct routes / methods / body', async () => {
  const { calls, restore } = stubFetch();
  try {
    const started = await api.preview.start('b1', 'main');
    await api.preview.get('b1');
    await api.preview.heartbeat('b1');
    await api.preview.stop('b1');

    // start: POST /api/boards/b1/preview with { branch }
    assert.ok(calls[0].url.endsWith('/api/boards/b1/preview'), `start url: ${calls[0].url}`);
    assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].body!, /"branch":"main"/);
    assert.equal(started.id, 'tpv-x');

    // get: GET /api/boards/b1/preview
    assert.ok(calls[1].url.endsWith('/api/boards/b1/preview'), `get url: ${calls[1].url}`);
    assert.equal(calls[1].method, 'GET');

    // heartbeat: POST /api/boards/b1/preview/heartbeat
    assert.ok(calls[2].url.endsWith('/api/boards/b1/preview/heartbeat'), `heartbeat url: ${calls[2].url}`);
    assert.equal(calls[2].method, 'POST');

    // stop: DELETE /api/boards/b1/preview
    assert.ok(calls[3].url.endsWith('/api/boards/b1/preview'), `stop url: ${calls[3].url}`);
    assert.equal(calls[3].method, 'DELETE');
  } finally {
    restore();
  }
});

test('preview.get resolves to null on 404 (no active preview)', async () => {
  const { restore } = stubFetch(404);
  try {
    const result = await api.preview.get('b1');
    assert.equal(result, null);
  } finally {
    restore();
  }
});
