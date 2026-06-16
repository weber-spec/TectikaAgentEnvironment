// Context-seeded "thinking" phrases for the chat live edge. Pure (no React).
// The live edge shows a rotating phrase so the agent never looks stuck during a
// long model call; the pool is chosen from what the agent most recently did.

import type { RunEvent } from './types';

export type PhraseContext = 'thinking' | 'exploring' | 'planning' | 'testing' | 'github';

export const PHRASE_POOLS: Record<PhraseContext, string[]> = {
  thinking: [
    'Percolating…', 'Untangling the threads…', 'Connecting the dots…', 'Mulling it over…',
    'Letting it simmer…', 'Thinking it through…', 'Chasing the idea…', 'Turning it over…',
  ],
  exploring: [
    'Reading the code…', 'Tracing the call path…', 'Getting the lay of the land…',
    'Following the imports…', 'Scanning for clues…', 'Mapping the board…',
  ],
  planning: [
    'Sketching the approach…', 'Weighing the options…', 'Shaping the change…',
    'Choosing sensible defaults…', 'Planning the next step…', 'Lining up the pieces…',
  ],
  testing: [
    'Running the suite…', 'Watching for red…', 'Checking edge cases…',
    'Letting the tests breathe…', 'Verifying nothing broke…',
  ],
  github: [
    'Talking to GitHub…', 'Syncing the branch…', 'Pushing the change…',
    'Opening the PR…', 'Wrangling git…',
  ],
};

/** Map a tool name to the phrase context it implies. Unknown → 'thinking'. */
export function toolContext(toolName: string | undefined | null): PhraseContext {
  if (!toolName) return 'thinking';
  const n = toolName.toLowerCase();
  if (n.includes('github') || n.includes('branch') || n.includes('_pr') || n.includes('pull_request') ||
      n.includes('push') || n.includes('commit') || n.includes('repo')) return 'github';
  if (n.includes('test')) return 'testing';
  if (n.includes('search') || n.includes('get_board') || n.includes('get_task') ||
      n.includes('get_artifact') || n.includes('read')) return 'exploring';
  if (n.includes('intent') || n.includes('brief') || n.includes('plan') || n.includes('write')) return 'planning';
  return 'thinking';
}

/** Pick a phrase from the pool, never repeating `last` back-to-back. */
export function pickPhrase(pool: string[], last: string | null): string {
  if (pool.length === 0) return '';
  if (pool.length === 1) return pool[0];
  let p: string | null = last;
  while (p === last) p = pool[Math.floor(Math.random() * pool.length)];
  return p!;
}

/** Derive the live-edge context from the most recent tool the agent invoked. */
export function contextFromEvents(events: RunEvent[]): PhraseContext {
  let latest: RunEvent | undefined;
  for (const e of events) {
    if (e.kind === 'ToolCall' && e.toolName && (!latest || e.timestamp > latest.timestamp)) latest = e;
  }
  return latest ? toolContext(latest.toolName) : 'thinking';
}

/** Real cumulative tokens for the live edge — summed from events that carry usage. */
export function sumTokens(events: RunEvent[]): number {
  return events.reduce((sum, e) => sum + (e.tokenUsage?.total ?? 0), 0);
}
