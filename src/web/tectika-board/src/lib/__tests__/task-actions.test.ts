// Tests the "Reset & run" orchestration (resetTaskForRerun): it must move the task back to
// Backlog AND clear the agent's Foundry conversation — not merely reset the usage session.
//
// Why this matters: a task whose Foundry conversation is poisoned (e.g. a dangling tool call)
// can only recover by clearing the conversation. The old "Reset & run" called /reset-usage,
// which leaves FoundryThreadId intact, so the poisoned thread survived the reset and the run
// failed again the same way. This test pins the fix: Reset goes through /clear.
//
// Like preview-api.test.ts, the real client imports './api' (which uses extensionless relative
// specifiers); register a resolve hook so Node's ESM loader appends '.ts'.
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

const { resetTaskForRerun, startTaskRun } = await import('../task-actions.ts');

interface Call { url: string; method: string; body?: string }

function stubFetch(): { calls: Call[]; restore: () => void } {
  const calls: Call[] = [];
  const orig = globalThis.fetch;
  globalThis.fetch = (async (url: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string | undefined });
    // All endpoints here return 200 with no body.
    return new Response('', { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('resetTaskForRerun moves the task to Backlog, then clears the conversation', async () => {
  const { calls, restore } = stubFetch();
  try {
    await resetTaskForRerun('b1', 't1');
  } finally {
    restore();
  }

  // 1) Status → Backlog (so a fresh run can start).
  const status = calls.find(c => c.url.endsWith('/tasks/t1/status'));
  assert.ok(status, 'should PATCH the task status');
  assert.equal(status!.method, 'PATCH');
  assert.match(status!.body ?? '', /"Backlog"/);

  // 2) The fix: clear the agent's Foundry conversation (nulls FoundryThreadId server-side).
  const clear = calls.find(c => c.url.endsWith('/tasks/t1/clear'));
  assert.ok(clear, 'should POST /clear to reset the Foundry conversation');
  assert.equal(clear!.method, 'POST');
});

test('resetTaskForRerun does NOT use /reset-usage (which preserves the poisoned thread)', async () => {
  const { calls, restore } = stubFetch();
  try {
    await resetTaskForRerun('b1', 't1');
  } finally {
    restore();
  }

  assert.ok(
    !calls.some(c => c.url.endsWith('/reset-usage')),
    'Reset & run must clear the conversation, not just reset the usage session',
  );
});

test('clear happens after the Backlog status change', async () => {
  const { calls, restore } = stubFetch();
  try {
    await resetTaskForRerun('b1', 't1');
  } finally {
    restore();
  }

  const statusIdx = calls.findIndex(c => c.url.endsWith('/tasks/t1/status'));
  const clearIdx = calls.findIndex(c => c.url.endsWith('/tasks/t1/clear'));
  assert.ok(statusIdx >= 0 && clearIdx >= 0, 'both calls should fire');
  assert.ok(statusIdx < clearIdx, 'status change should precede the clear');
});

// startTaskRun powers the board run: failed tasks (reset=true) get a fresh conversation,
// Backlog tasks (reset=false) start as-is.
test('startTaskRun(reset=true) clears the conversation before starting the run', async () => {
  const { calls, restore } = stubFetch();
  try {
    await startTaskRun('b1', 't1', { reset: true });
  } finally {
    restore();
  }

  const clearIdx = calls.findIndex(c => c.url.endsWith('/tasks/t1/clear'));
  const startIdx = calls.findIndex(c => c.url.endsWith('/runs/start'));
  assert.ok(clearIdx >= 0, 'should clear the Foundry conversation for a retried task');
  assert.ok(startIdx >= 0, 'should start a run');
  assert.ok(clearIdx < startIdx, 'clear must precede the run start');
});

test('startTaskRun(reset=false) starts directly — no clear, no status change', async () => {
  const { calls, restore } = stubFetch();
  try {
    await startTaskRun('b1', 't1', { reset: false });
  } finally {
    restore();
  }

  assert.ok(!calls.some(c => c.url.endsWith('/clear')), 'a non-retry task keeps its conversation');
  assert.ok(!calls.some(c => c.url.endsWith('/status')), 'a non-retry task keeps its status');
  assert.ok(calls.some(c => c.url.endsWith('/runs/start')), 'should start the run');
});
