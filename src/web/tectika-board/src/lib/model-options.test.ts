import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildModelOptions, DEFAULT_MODEL_OPTION } from './model-options.ts';

test('ready: Default sentinel first, then the fetched models', () => {
  const opts = buildModelOptions({ models: ['gpt-4o', 'o3'], saved: '', status: 'ready' });
  assert.deepEqual(opts, [
    DEFAULT_MODEL_OPTION,
    { value: 'gpt-4o', label: 'gpt-4o' },
    { value: 'o3', label: 'o3' },
  ]);
});

test('ready: a saved model not in the live list is preserved (appended)', () => {
  const opts = buildModelOptions({ models: ['gpt-4o', 'o3'], saved: 'legacy-model', status: 'ready' });
  assert.deepEqual(opts.map(o => o.value), ['', 'gpt-4o', 'o3', 'legacy-model']);
});

test('ready: a saved model already in the list is not duplicated', () => {
  const opts = buildModelOptions({ models: ['gpt-4o', 'o3'], saved: 'gpt-4o', status: 'ready' });
  assert.deepEqual(opts.map(o => o.value), ['', 'gpt-4o', 'o3']);
});

test('error: only Default + the saved value (no invented list)', () => {
  const opts = buildModelOptions({ models: [], saved: 'gpt-4o', status: 'error' });
  assert.deepEqual(opts.map(o => o.value), ['', 'gpt-4o']);
});

test('error with no saved value: just Default', () => {
  const opts = buildModelOptions({ models: [], saved: '', status: 'error' });
  assert.deepEqual(opts, [DEFAULT_MODEL_OPTION]);
});

test('loading: ignores models, shows Default + saved so the current value renders', () => {
  const opts = buildModelOptions({ models: ['gpt-4o'], saved: 'claude-opus-4-8', status: 'loading' });
  assert.deepEqual(opts.map(o => o.value), ['', 'claude-opus-4-8']);
});
