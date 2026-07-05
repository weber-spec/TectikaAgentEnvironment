import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildClaudeModelOptions, CLAUDE_MODEL_IDS, DEFAULT_CLAUDE_MODEL } from './claude-model-options.ts';

test('ready: Default (Opus) leads, then the live models, deduped', () => {
  const opts = buildClaudeModelOptions({ models: ['claude-fable-5', 'claude-opus-4-8'], saved: 'claude-opus-4-8', status: 'ready' });
  assert.deepEqual(opts.map(o => o.value), ['claude-opus-4-8', 'claude-fable-5']);
});

test('ready: an unknown live id is included and labelled with the raw id', () => {
  const opts = buildClaudeModelOptions({ models: ['claude-future-9'], saved: DEFAULT_CLAUDE_MODEL, status: 'ready' });
  const future = opts.find(o => o.value === 'claude-future-9');
  assert.ok(future);
  assert.equal(future!.label, 'claude-future-9');
});

test('error: falls back to the curated list, which includes Fable 5', () => {
  const opts = buildClaudeModelOptions({ models: [], saved: DEFAULT_CLAUDE_MODEL, status: 'error' });
  assert.ok(opts.some(o => o.value === 'claude-fable-5'));
  assert.deepEqual(
    opts.map(o => o.value),
    [DEFAULT_CLAUDE_MODEL, ...CLAUDE_MODEL_IDS.filter(id => id !== DEFAULT_CLAUDE_MODEL)],
  );
});

test('loading: ignores live models, uses curated fallback', () => {
  const opts = buildClaudeModelOptions({ models: ['claude-live-only'], saved: DEFAULT_CLAUDE_MODEL, status: 'loading' });
  assert.ok(!opts.some(o => o.value === 'claude-live-only'));
  assert.ok(opts.some(o => o.value === 'claude-fable-5'));
});

test('ready but empty live list falls back to curated', () => {
  const opts = buildClaudeModelOptions({ models: [], saved: DEFAULT_CLAUDE_MODEL, status: 'ready' });
  assert.ok(opts.some(o => o.value === 'claude-fable-5'));
});

test('a saved model not in the live list is preserved (appended)', () => {
  const opts = buildClaudeModelOptions({ models: ['claude-opus-4-8'], saved: 'legacy-claude-x', status: 'ready' });
  assert.equal(opts[opts.length - 1].value, 'legacy-claude-x');
});
