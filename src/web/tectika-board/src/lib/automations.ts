// Automation recipes — the "When [trigger], then [action]" model, plus a tiny
// client-side engine that runs recipes when board events fire.

import type { AgentTask, AgentTaskStatus, TaskPriority, AutomationRecipe, TriggerType, ActionType } from './types';

export const TRIGGER_META: Record<TriggerType, { label: string; needsStatus?: boolean; needsPriority?: boolean }> = {
  statusChanges:   { label: 'a status changes' },
  statusBecomes:   { label: 'status becomes', needsStatus: true },
  priorityBecomes: { label: 'priority becomes', needsPriority: true },
  itemCreated:     { label: 'an item is created' },
  dateArrives:     { label: 'a due date arrives' },
  personAssigned:  { label: 'a person is assigned' },
  artifactUpdated: { label: 'an artifact is updated' },
};

export const ACTION_META: Record<ActionType, { label: string; needsStatus?: boolean; needsPriority?: boolean; needsText?: boolean }> = {
  notify:         { label: 'notify the team', needsText: true },
  setStatus:      { label: 'set status to', needsStatus: true },
  setPriority:    { label: 'set priority to', needsPriority: true },
  assign:         { label: 'assign to', needsText: true },
  createItem:     { label: 'create an item', needsText: true },
  moveToGroup:    { label: 'move to group', needsText: true },
  createApproval: { label: 'request approval' },
  runAgent:       { label: 'run the assigned agent' },
};

export interface AutomationEvent {
  type: TriggerType;
  task: AgentTask;
}

export interface AutomationHandlers {
  setStatus?: (taskId: string, status: AgentTaskStatus) => void;
  setPriority?: (taskId: string, priority: TaskPriority) => void;
  notify?: (message: string) => void;
}

/** Run all enabled recipes whose trigger matches the event. Returns # actions fired. */
export function runAutomations(recipes: AutomationRecipe[], event: AutomationEvent, handlers: AutomationHandlers): number {
  let fired = 0;
  for (const r of recipes) {
    if (!r.enabled) continue;
    if (!triggerMatches(r, event)) continue;

    for (const action of r.actions) {
      switch (action.type) {
        case 'setStatus':
          if (action.value && handlers.setStatus) { handlers.setStatus(event.task.id, action.value as AgentTaskStatus); fired++; }
          break;
        case 'setPriority':
          if (action.value && handlers.setPriority) { handlers.setPriority(event.task.id, action.value as TaskPriority); fired++; }
          break;
        case 'notify':
          handlers.notify?.(action.value || `Automation: "${event.task.title}" matched a recipe`);
          fired++;
          break;
        default:
          handlers.notify?.(`Automation ran: ${ACTION_META[action.type].label} (simulated)`);
          fired++;
      }
    }
  }
  return fired;
}

function triggerMatches(r: AutomationRecipe, event: AutomationEvent): boolean {
  const tr = r.trigger;
  if (tr.type === 'statusChanges') return event.type === 'statusBecomes' || event.type === 'statusChanges';
  if (tr.type === 'statusBecomes') return event.type === 'statusBecomes' && event.task.status === tr.value;
  if (tr.type === 'priorityBecomes') return event.task.priority === tr.value;
  return tr.type === event.type;
}

export function describeRecipe(r: AutomationRecipe): string {
  const t = TRIGGER_META[r.trigger.type];
  const trigger = t.needsStatus || t.needsPriority ? `${t.label} ${r.trigger.value ?? ''}`.trim() : t.label;
  const actions = r.actions.map(a => {
    const m = ACTION_META[a.type];
    return m.needsStatus || m.needsPriority || m.needsText ? `${m.label} ${a.value ?? ''}`.trim() : m.label;
  }).join(', then ');
  return `When ${trigger}, ${actions || '…'}`;
}
