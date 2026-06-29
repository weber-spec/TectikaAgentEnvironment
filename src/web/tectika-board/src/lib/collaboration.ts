// Collaboration data — the people roster plus seed generators for the comments
// and activity feed (persisted client-side, since the backend has no schema for them).

import type { AgentRole, AgentTask, Person, Comment, ActivityEntry } from './types';
import { colorFor, STATUS_CONFIG } from './palette';
import { displayName } from './format';

/** Human team members (the backend has no users endpoint — this is the demo roster). */
export const HUMAN_ROSTER: Person[] = [
  { id: 'eli@tectika.com', name: 'Eli Weber', kind: 'Human', hex: '#0073ea', title: 'Platform Lead' },
  { id: 'maya@tectika.com', name: 'Maya Cohen', kind: 'Human', hex: '#e2445c', title: 'Product Manager' },
  { id: 'noah@tectika.com', name: 'Noah Levi', kind: 'Human', hex: '#00c875', title: 'Eng Manager' },
  { id: 'lena@tectika.com', name: 'Lena Adler', kind: 'Human', hex: '#a25ddc', title: 'QA Lead' },
  { id: 'dev@tectika.com', name: 'Dev User', kind: 'Human', hex: '#fdab3d', title: 'Developer' },
];

export const CURRENT_USER = HUMAN_ROSTER[0];

/** Build the lookup of all people: agent roles + humans (+ any unknown assignees). */
export function buildPeople(roles: AgentRole[], tasks: AgentTask[]): Record<string, Person> {
  const map: Record<string, Person> = {};
  for (const h of HUMAN_ROSTER) map[h.id] = h;
  for (const r of roles) {
    map[r.id] = { id: r.id, name: r.displayName, kind: 'Agent', hex: colorFor(r.id), title: r.modelOverride };
  }
  for (const t of tasks) {
    if (!map[t.assignee.id]) {
      map[t.assignee.id] = {
        id: t.assignee.id,
        name: displayName(t.assignee.id),
        kind: t.assignee.type,
        hex: colorFor(t.assignee.id),
      };
    }
  }
  return map;
}

let seq = 0;
export function uid(prefix = 'c'): string {
  seq += 1;
  return `${prefix}-${Date.now().toString(36)}-${seq}`;
}

/** Deterministic-ish seed comments + activity so item panels feel alive on first load. */
export function seedCollaboration(tasks: AgentTask[]): { comments: Comment[]; activity: ActivityEntry[] } {
  const comments: Comment[] = [];
  const activity: ActivityEntry[] = [];

  for (const t of tasks) {
    const created = new Date(t.createdAt).getTime();
    activity.push({
      id: uid('a'), taskId: t.id, kind: 'created', actorId: t.createdBy || 'dev@tectika.com',
      createdAt: new Date(created).toISOString(),
    });

    // A couple of tasks get a richer thread.
    if (t.status === 'InProgress' || t.status === 'Review') {
      activity.push({
        id: uid('a'), taskId: t.id, kind: 'status', actorId: 'noah@tectika.com',
        from: 'Backlog', to: STATUS_CONFIG[t.status].label,
        createdAt: new Date(created + 3600_000).toISOString(),
      });
    }
  }
  return { comments, activity };
}
