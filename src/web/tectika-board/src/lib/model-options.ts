// Option model for the agent model picker. Kept as a pure helper so the
// loading / ready / error behavior is unit-tested independently of React.

export type ModelFetchStatus = 'loading' | 'ready' | 'error';

export interface ModelOption {
  /** The value written to AgentRole.modelOverride. '' ⇒ use the project default model. */
  value: string;
  label: string;
}

/** Sentinel meaning "no override — use the Foundry project's default model". */
export const DEFAULT_MODEL_OPTION: ModelOption = { value: '', label: 'Default' };

/**
 * Build the picker's options:
 * - Always starts with the Default sentinel.
 * - When the live list is `ready`, includes the fetched models.
 * - Always guarantees the currently-saved value is present (so editing an agent
 *   never silently drops a model that's no longer in the live list).
 * On `loading`/`error` the fetched models are ignored — only Default + saved show.
 */
export function buildModelOptions(
  { models, saved, status }: { models: string[]; saved: string; status: ModelFetchStatus },
): ModelOption[] {
  const options: ModelOption[] = [DEFAULT_MODEL_OPTION];
  if (status === 'ready') {
    for (const m of models) {
      if (m && !options.some(o => o.value === m)) options.push({ value: m, label: m });
    }
  }
  const savedValue = saved.trim();
  if (savedValue && !options.some(o => o.value === savedValue)) {
    options.push({ value: savedValue, label: savedValue });
  }
  return options;
}
