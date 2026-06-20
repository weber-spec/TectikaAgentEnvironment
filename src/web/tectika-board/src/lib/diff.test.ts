import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseUnifiedDiff } from './diff.ts';

test('classifies add / del / context / hunk lines', () => {
  const patch = '@@ -1,2 +1,2 @@\n context\n-old\n+new';
  const lines = parseUnifiedDiff(patch);
  assert.equal(lines[0].type, 'hunk');
  assert.equal(lines[1].type, 'context');
  assert.equal(lines[2].type, 'del');
  assert.equal(lines[2].text, 'old');
  assert.equal(lines[3].type, 'add');
  assert.equal(lines[3].text, 'new');
});

test('empty / null patch -> empty array', () => {
  assert.deepEqual(parseUnifiedDiff(''), []);
  assert.deepEqual(parseUnifiedDiff(null), []);
});

test('strips the leading +/-/space marker from text', () => {
  const lines = parseUnifiedDiff('+added line');
  assert.equal(lines[0].type, 'add');
  assert.equal(lines[0].text, 'added line');
});
