// The client half of the dependency rule: which tasks a board run launches.
//
// The bug this pins: the old filter excluded any task with an incoming Dependency edge, trusting the
// backend cascade to start it once its parents finished. But that cascade only fires on a parent's
// *transition* to Done — so a dependency drawn after the parent had already finished stranded the child
// in Backlog forever. Nothing would ever start it again except a manual per-task run.
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

const { computeUnmetUpstreamIds, isRunnableOnBoard } = await import('../board-scheduling.ts');
type AgentTask = import('../types.ts').AgentTask;
type AgentTaskStatus = import('../types.ts').AgentTaskStatus;

function task(id: string, status: AgentTaskStatus, assignee: 'Agent' | 'Human' = 'Agent'): AgentTask {
  return { id, status, assignee: { type: assignee, id: 'agent-x' } } as AgentTask;
}

function byId(...tasks: AgentTask[]): Record<string, AgentTask> {
  return Object.fromEntries(tasks.map(t => [t.id, t]));
}

test('a task whose parents are all Done is runnable — even if the edge was added afterwards', () => {
  const parent = task('parent', 'Done');
  const child = task('child', 'Backlog');
  const unmet = computeUnmetUpstreamIds({ child: ['parent'] }, byId(parent, child));

  assert.deepEqual(unmet, {}, 'a satisfied dependency is not "unmet"');
  assert.equal(isRunnableOnBoard(child, unmet), true);
});

test('a task with an unfinished parent is not runnable — the cascade will start it', () => {
  const parent = task('parent', 'InProgress');
  const child = task('child', 'Backlog');
  const unmet = computeUnmetUpstreamIds({ child: ['parent'] }, byId(parent, child));

  assert.deepEqual(unmet, { child: ['parent'] });
  assert.equal(isRunnableOnBoard(child, unmet), false);
});

test('fan-in: one unfinished parent among several blocks the task', () => {
  const done = task('done', 'Done');
  const busy = task('busy', 'Review');
  const child = task('child', 'Backlog');
  const unmet = computeUnmetUpstreamIds({ child: ['done', 'busy'] }, byId(done, busy, child));

  assert.deepEqual(unmet, { child: ['busy'] });
  assert.equal(isRunnableOnBoard(child, unmet), false);
});

// Matches the server, which treats a missing upstream document as not-Done: we can't prove its work
// happened, so we don't start on top of it.
test('a parent that no longer exists (dangling edge) counts as unmet', () => {
  const child = task('child', 'Backlog');
  const unmet = computeUnmetUpstreamIds({ child: ['ghost'] }, byId(child));

  assert.deepEqual(unmet, { child: ['ghost'] });
  assert.equal(isRunnableOnBoard(child, unmet), false);
});

// Failed tasks are reset to Backlog and retried by the board run. Before this fix a Failed *child*
// was doubly stranded: the board run skipped it for having an edge, and the cascade only starts
// children that are in Backlog.
test('a Failed task with satisfied dependencies is runnable (reset & retry)', () => {
  const parent = task('parent', 'Done');
  const child = task('child', 'Failed');
  const unmet = computeUnmetUpstreamIds({ child: ['parent'] }, byId(parent, child));

  assert.equal(isRunnableOnBoard(child, unmet), true);
});

test('human-owned and already-running tasks are never part of a board run', () => {
  assert.equal(isRunnableOnBoard(task('h', 'Backlog', 'Human'), {}), false);
  assert.equal(isRunnableOnBoard(task('r', 'InProgress'), {}), false);
  assert.equal(isRunnableOnBoard(task('d', 'Done'), {}), false);
});

test('a root with no dependencies is runnable', () => {
  assert.equal(isRunnableOnBoard(task('root', 'Backlog'), {}), true);
});
