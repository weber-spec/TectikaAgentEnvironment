// The client's mirror of the server's TaskStartGate (Core/Scheduling/TaskStartGate.cs): which tasks
// are ready to make progress right now. Kept pure and out of the React layer so the rule is testable
// without rendering — and so there is exactly one definition of "unmet dependency" on this side.

import type { AgentTask } from './types';

/**
 * Dependency parents (per `upstreamIds`, which already excludes QaFeedback edges) that are not Done.
 * A parent that isn't in `tasksById` at all — a dangling edge left by a deleted task — counts as
 * unmet, exactly as the server treats a missing upstream document: we can't prove its work happened.
 *
 * Only tasks with at least one unmet parent get a key, so `!unmetUpstreamIds[id]` reads as "ready".
 */
export function computeUnmetUpstreamIds(
  upstreamIds: Record<string, string[]>,
  tasksById: Record<string, AgentTask>,
): Record<string, string[]> {
  const unmet: Record<string, string[]> = {};
  for (const [taskId, parents] of Object.entries(upstreamIds)) {
    const blocking = parents.filter(id => tasksById[id]?.status !== 'Done');
    if (blocking.length > 0) unmet[taskId] = blocking;
  }
  return unmet;
}

/**
 * Eligible for a board run: an agent-owned task that can make progress — Backlog (start fresh) or
 * Failed (reset to Backlog, then retry) — with every Dependency parent already Done.
 *
 * Note what this is NOT: "has no incoming dependency edge". That was the old rule, and it stranded
 * tasks. The cascade that was supposed to launch them only fires on a parent's *transition* to Done,
 * so a dependency drawn after the parent had already finished left the child in Backlog forever —
 * no cascade would ever fire again, and the board run skipped it for having an edge at all. A task
 * whose parents are still working is still excluded here; the cascade starts it when they finish.
 */
export function isRunnableOnBoard(
  task: AgentTask,
  unmetUpstreamIds: Record<string, string[]>,
): boolean {
  return task.assignee?.type === 'Agent'
    && (task.status === 'Backlog' || task.status === 'Failed')
    && !unmetUpstreamIds[task.id];
}
