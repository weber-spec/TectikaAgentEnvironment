import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseMentions, countUnread, splitComments } from './team-notes.ts';
import type { Comment, Person } from './types.ts';

const roster: Person[] = [
  { id: 'eli@tectika.com', name: 'Eli Weber', kind: 'Human', hex: '#0073ea' },
  { id: 'maya@tectika.com', name: 'Maya Cohen', kind: 'Human', hex: '#e2445c' },
];

test('parseMentions resolves @first-name and @email to ids', () => {
  assert.deepEqual(parseMentions('hey @maya and @eli@tectika.com', roster).sort(),
    ['eli@tectika.com', 'maya@tectika.com']);
});

test('parseMentions ignores unknown handles', () => {
  assert.deepEqual(parseMentions('hi @nobody', roster), []);
});

const mk = (over: Partial<Comment>): Comment => ({
  id: Math.random().toString(), taskId: 't1', boardId: 'b1', kind: 'message',
  authorId: 'maya@tectika.com', body: 'x', mentions: [], createdAt: '2026-06-29T10:00:00Z', ...over,
});

test('countUnread counts others\' comments after lastReadAt', () => {
  const comments = [
    mk({ createdAt: '2026-06-29T09:00:00Z' }),
    mk({ createdAt: '2026-06-29T11:00:00Z' }),
    mk({ createdAt: '2026-06-29T12:00:00Z', authorId: 'eli@tectika.com' }),
  ];
  assert.equal(countUnread(comments, '2026-06-29T10:30:00Z', 'eli@tectika.com'), 1);
});

test('countUnread excludes deleted and counts all when never read', () => {
  const comments = [mk({}), mk({ deletedAt: '2026-06-29T10:05:00Z' })];
  assert.equal(countUnread(comments, undefined, 'eli@tectika.com'), 1);
});

test('splitComments separates notes (non-deleted) from messages', () => {
  const comments = [
    mk({ kind: 'note', noteType: 'decision' }),
    mk({ kind: 'note', deletedAt: '2026-06-29T10:05:00Z' }),
    mk({ kind: 'message' }),
  ];
  const { notes, messages } = splitComments(comments);
  assert.equal(notes.length, 1);
  assert.equal(messages.length, 1);
});
