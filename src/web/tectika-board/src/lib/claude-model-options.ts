// Option model for the Claude Code model picker. Kept as a pure helper so the
// loading / ready / error + curated-fallback behavior is unit-tested independently of React.
// The live list comes from GET /api/connections/{id}/models (Anthropic /v1/models); this module
// supplies the labels for known ids and the offline fallback used for OAuth connections or on error.

import type { ModelFetchStatus, ModelOption } from './model-options';

/** Claude Code model choices (CLI `--model`). Labels for known ids + the curated offline fallback list.
 *  A live fetch supersedes this whenever it succeeds. The default for a new Claude agent is Opus 4.8. */
export const CLAUDE_MODELS: { id: string; label: string }[] = [
  { id: 'claude-fable-5', label: 'Claude Fable 5' },
  { id: 'claude-opus-4-8', label: 'Claude Opus 4.8' },
  { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6' },
  { id: 'claude-haiku-4-5-20251001', label: 'Claude Haiku 4.5' },
];
export const DEFAULT_CLAUDE_MODEL = 'claude-opus-4-8';
export const CLAUDE_MODEL_IDS = CLAUDE_MODELS.map(m => m.id);

const LABELS: Record<string, string> = Object.fromEntries(CLAUDE_MODELS.map(m => [m.id, m.label]));

/**
 * Build the Claude model picker's options:
 * - Unlike buildModelOptions there is NO '' Default sentinel — Claude always sends a concrete model id.
 * - When the live list is `ready` (non-empty) it drives the options; otherwise the curated fallback is used.
 * - DEFAULT_CLAUDE_MODEL leads, then the (live or fallback) list.
 * - The currently-saved value is always guaranteed present (so editing never drops a model no longer listed).
 * Known ids get a friendly label; unknown live ids fall back to the raw id.
 */
export function buildClaudeModelOptions(
  { models, saved, status }: { models: string[]; saved: string; status: ModelFetchStatus },
): ModelOption[] {
  const ids = status === 'ready' && models.length > 0 ? models : CLAUDE_MODEL_IDS;
  const options: ModelOption[] = [];
  const push = (id: string) => {
    if (id && !options.some(o => o.value === id)) options.push({ value: id, label: LABELS[id] ?? id });
  };
  push(DEFAULT_CLAUDE_MODEL);
  for (const id of ids) push(id);
  const savedValue = saved.trim();
  if (savedValue) push(savedValue);
  return options;
}
