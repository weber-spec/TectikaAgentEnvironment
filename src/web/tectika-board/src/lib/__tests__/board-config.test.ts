// Run: node --test --experimental-transform-types src/lib/__tests__/board-config.test.ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cloneBoardConfig } from '../board-config.ts';

function fakeLocalStorage() {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => (m.has(k) ? m.get(k)! : null),
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  } as unknown as Storage;
}

test('cloneBoardConfig copies the source board config to the new board key', () => {
  const ls = fakeLocalStorage();
  ls.setItem('tectika:board:src', JSON.stringify({ activeViewId: 'v-kanban' }));
  cloneBoardConfig('src', 'dst', ls);
  assert.equal(ls.getItem('tectika:board:dst'), JSON.stringify({ activeViewId: 'v-kanban' }));
});

test('cloneBoardConfig is a no-op when the source has no saved config', () => {
  const ls = fakeLocalStorage();
  cloneBoardConfig('src', 'dst', ls);
  assert.equal(ls.getItem('tectika:board:dst'), null);
});
