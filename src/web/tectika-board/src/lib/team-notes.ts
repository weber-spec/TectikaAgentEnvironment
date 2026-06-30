import type { Comment, Person } from './types';

/** Resolve @handles in a body to known person ids. Matches @email or @first-name (case-insensitive). */
export function parseMentions(body: string, roster: Person[]): string[] {
  const handles = body.match(/@[\w.+-]+(@[\w.-]+)?/g) ?? [];
  const ids = new Set<string>();
  for (const raw of handles) {
    const h = raw.slice(1).toLowerCase();
    const match = roster.find(p =>
      p.id.toLowerCase() === h ||
      p.name.split(' ')[0].toLowerCase() === h);
    if (match) ids.add(match.id);
  }
  return [...ids];
}

/** Count non-deleted comments authored by others, created after lastReadAt (all if never read). */
export function countUnread(comments: Comment[], lastReadAt: string | undefined, currentUserId: string): number {
  const since = lastReadAt ? Date.parse(lastReadAt) : 0;
  return comments.filter(c =>
    !c.deletedAt &&
    c.authorId !== currentUserId &&
    Date.parse(c.createdAt) > since).length;
}

/** Split into the Notes zone (non-deleted notes) and the Discussion feed (messages, incl. tombstones). */
export function splitComments(comments: Comment[]): { notes: Comment[]; messages: Comment[] } {
  const notes = comments.filter(c => c.kind === 'note' && !c.deletedAt);
  const messages = comments.filter(c => c.kind === 'message');
  return { notes, messages };
}
