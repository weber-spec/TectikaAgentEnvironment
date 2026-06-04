'use client';

import React, { useState } from 'react';
import { useBoard } from '@/lib/board-context';
import type { AutomationRecipe, TriggerType, ActionType } from '@/lib/types';
import { TRIGGER_META, ACTION_META, describeRecipe } from '@/lib/automations';
import { STATUS_ORDER, STATUS_CONFIG, PRIORITY_ORDER, PRIORITY_CONFIG } from '@/lib/palette';
import { Modal } from '@/components/ui/overlays';
import { Button, Toggle } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { uid } from '@/lib/collaboration';

const TEMPLATES: { label: string; recipe: () => Omit<AutomationRecipe, 'id' | 'runs' | 'createdAt'> }[] = [
  { label: 'When status becomes Done, notify the team', recipe: () => ({ enabled: true, trigger: { type: 'statusBecomes', value: 'Done' }, conditions: [], actions: [{ type: 'notify', value: 'A task was completed 🎉' }] }) },
  { label: 'When status becomes Stuck, set priority to High', recipe: () => ({ enabled: true, trigger: { type: 'statusBecomes', value: 'Blocked' }, conditions: [], actions: [{ type: 'setPriority', value: 'High' }] }) },
  { label: 'When an item is created, set status to Backlog', recipe: () => ({ enabled: true, trigger: { type: 'itemCreated' }, conditions: [], actions: [{ type: 'setStatus', value: 'Backlog' }] }) },
  { label: 'When status becomes In Review, request approval', recipe: () => ({ enabled: true, trigger: { type: 'statusBecomes', value: 'Review' }, conditions: [], actions: [{ type: 'createApproval' }] }) },
];

export function AutomationsModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { automations, saveAutomation, deleteAutomation, toggleAutomation } = useBoard();
  const [building, setBuilding] = useState(false);

  return (
    <Modal open={open} onClose={onClose} width={680} title={<span className="flex items-center gap-2"><Icon.bolt size={18} className="text-[#fdab3d]" /> Automations</span>}>
      {!building ? (
        <>
          <div className="flex items-center justify-between mb-3">
            <p className="text-sm text-[var(--muted)]">Recipes run automatically when board events fire.</p>
            <Button variant="primary" size="sm" onClick={() => setBuilding(true)}><Icon.plus size={15} /> New automation</Button>
          </div>

          {automations.length > 0 && (
            <div className="flex flex-col gap-2 mb-5">
              {automations.map(a => (
                <div key={a.id} className="flex items-center gap-3 p-3 rounded-lg border border-[var(--border)] bg-[var(--surface)]/40">
                  <Toggle checked={a.enabled} onChange={() => toggleAutomation(a.id)} />
                  <div className="flex-1 min-w-0">
                    <div className="text-[13px] text-[var(--foreground)]">{describeRecipe(a)}</div>
                    <div className="text-[11px] text-[var(--muted)]">Ran {a.runs} time{a.runs !== 1 ? 's' : ''}</div>
                  </div>
                  <button onClick={() => deleteAutomation(a.id)} className="text-[var(--muted)] hover:text-[#e2445c]"><Icon.trash size={16} /></button>
                </div>
              ))}
            </div>
          )}

          <div className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold mb-2">Templates</div>
          <div className="grid grid-cols-1 gap-2">
            {TEMPLATES.map((t, i) => (
              <button key={i} onClick={() => saveAutomation({ ...t.recipe(), id: uid('auto'), runs: 0, createdAt: new Date().toISOString() })}
                className="flex items-center gap-2 p-2.5 rounded-lg border border-[var(--border)] hover:border-[var(--primary)] hover:bg-[var(--primary-light)] text-left transition-colors">
                <Icon.bolt size={15} className="text-[#fdab3d] shrink-0" />
                <span className="text-[13px] text-[var(--foreground)]">{t.label}</span>
                <Icon.plus size={15} className="ml-auto text-[var(--muted)]" />
              </button>
            ))}
          </div>
        </>
      ) : (
        <Builder onSave={r => { saveAutomation({ ...r, id: uid('auto'), runs: 0, createdAt: new Date().toISOString() }); setBuilding(false); }} onCancel={() => setBuilding(false)} />
      )}
    </Modal>
  );
}

function Builder({ onSave, onCancel }: { onSave: (r: Omit<AutomationRecipe, 'id' | 'runs' | 'createdAt'>) => void; onCancel: () => void }) {
  const [trigger, setTrigger] = useState<TriggerType>('statusBecomes');
  const [triggerVal, setTriggerVal] = useState('Done');
  const [action, setAction] = useState<ActionType>('notify');
  const [actionVal, setActionVal] = useState('Nice work!');

  const tMeta = TRIGGER_META[trigger];
  const aMeta = ACTION_META[action];

  const valueSelect = (kind: 'status' | 'priority' | undefined, val: string, set: (v: string) => void) => {
    if (kind === 'status') return <select value={val} onChange={e => set(e.target.value)} className="chip-select">{STATUS_ORDER.map(s => <option key={s} value={s}>{STATUS_CONFIG[s].label}</option>)}</select>;
    if (kind === 'priority') return <select value={val} onChange={e => set(e.target.value)} className="chip-select">{PRIORITY_ORDER.map(p => <option key={p} value={p}>{PRIORITY_CONFIG[p].label}</option>)}</select>;
    return null;
  };

  return (
    <div>
      <div className="bg-[var(--surface)]/50 rounded-xl p-4 text-[15px] leading-9 text-[var(--foreground)]">
        <span className="font-semibold">When </span>
        <select value={trigger} onChange={e => setTrigger(e.target.value as TriggerType)} className="chip-select chip-blue">
          {Object.entries(TRIGGER_META).map(([k, m]) => <option key={k} value={k}>{m.label}</option>)}
        </select>
        {' '}
        {tMeta.needsStatus && valueSelect('status', triggerVal, setTriggerVal)}
        {tMeta.needsPriority && valueSelect('priority', triggerVal, setTriggerVal)}
        <span className="font-semibold">, then </span>
        <select value={action} onChange={e => setAction(e.target.value as ActionType)} className="chip-select chip-green">
          {Object.entries(ACTION_META).map(([k, m]) => <option key={k} value={k}>{m.label}</option>)}
        </select>
        {' '}
        {aMeta.needsStatus && valueSelect('status', actionVal, setActionVal)}
        {aMeta.needsPriority && valueSelect('priority', actionVal, setActionVal)}
        {aMeta.needsText && <input value={actionVal} onChange={e => setActionVal(e.target.value)} placeholder="message" className="chip-select" style={{ minWidth: 160 }} />}
      </div>
      <div className="flex justify-end gap-2 mt-4">
        <Button size="sm" onClick={onCancel}>Cancel</Button>
        <Button variant="primary" size="sm" onClick={() => onSave({
          enabled: true,
          trigger: { type: trigger, value: (tMeta.needsStatus || tMeta.needsPriority) ? triggerVal : undefined },
          conditions: [],
          actions: [{ type: action, value: (aMeta.needsStatus || aMeta.needsPriority || aMeta.needsText) ? actionVal : undefined }],
        })}>Create automation</Button>
      </div>
    </div>
  );
}
