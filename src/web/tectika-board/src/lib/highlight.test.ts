import { test } from 'node:test';
import assert from 'node:assert/strict';
import { languageForPath } from './highlight.ts';

test('maps known extensions to shiki languages', () => {
  assert.equal(languageForPath('src/Cart.tsx'), 'tsx');
  assert.equal(languageForPath('a/b/main.ts'), 'typescript');
  assert.equal(languageForPath('x.py'), 'python');
  assert.equal(languageForPath('Program.cs'), 'csharp');
  assert.equal(languageForPath('data.json'), 'json');
  assert.equal(languageForPath('README.md'), 'markdown');
});

test('unknown or missing extension falls back to text', () => {
  assert.equal(languageForPath('LICENSE'), 'text');
  assert.equal(languageForPath('weird.xyz'), 'text');
  assert.equal(languageForPath(''), 'text');
});
