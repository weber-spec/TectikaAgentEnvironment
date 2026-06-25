// Task actions that orchestrate a sequence of API calls. Kept out of the React layer so the
// orchestration (the order and which endpoints are hit) is unit-testable without rendering.

import { api } from './api';

/**
 * The API-side reset for "Reset & run": move the task back to Backlog, then clear the agent's
 * conversation so the next run starts from a clean slate.
 *
 * "Clear" (POST /clear → ChatService.ClearAsync) nulls the task's FoundryThreadId, wipes the
 * brief, sets a transcript boundary, AND starts a fresh usage session — so it subsumes the
 * old /reset-usage call this used to make. The conversation reset is the important part: a
 * task whose Foundry conversation is poisoned (e.g. a dangling tool call) keeps failing the
 * same way until the thread is cleared, so a "fresh run" that preserved the thread could not
 * recover it. Routing Reset through /clear makes it a true fresh start.
 */
export async function resetTaskForRerun(boardId: string, taskId: string): Promise<void> {
  await api.tasks.updateStatus(boardId, taskId, 'Backlog');
  await api.tasks.clear(boardId, taskId);
}

/**
 * Start a run for one task as part of a board run. When `reset` is true (a Failed task being
 * retried) the agent's conversation is cleared first — a fresh start, same as the per-task
 * "Reset & run" — so a poisoned thread can't carry a failure into the retry. Backlog tasks
 * (`reset` false) start as-is and keep any existing conversation.
 */
export async function startTaskRun(
  boardId: string,
  taskId: string,
  { reset }: { reset: boolean },
): Promise<{ runId: string; taskId: string; status: string; streamUrl: string }> {
  if (reset) await resetTaskForRerun(boardId, taskId);
  return api.runs.start(boardId, taskId);
}
