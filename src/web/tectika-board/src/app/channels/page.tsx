'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import { useSettings } from '@/lib/settings-context';
import { ChannelsProvider, useChannels } from '@/lib/channels-context';
import type { Channel, Person } from '@/lib/types';
import { Avatar, Button, EmptyState } from '@/components/ui/primitives';
import { Modal } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { ChannelView } from '@/components/channels/ChannelView';

export default function ChannelsPage() {
  return (
    <Suspense>
      <ChannelsProvider>
        <ChannelsPageInner />
      </ChannelsProvider>
    </Suspense>
  );
}

function ChannelsPageInner() {
  const { t } = useSettings();
  const params = useSearchParams();
  const { channels, dms, loading, meId, person, peopleById, createChannel, openDm, addMember } = useChannels();
  const [activeId, setActiveId] = useState<string | null>(null);
  const [newChannelOpen, setNewChannelOpen] = useState(false);
  const [dmOpen, setDmOpen] = useState(false);
  const [addMemberOpen, setAddMemberOpen] = useState(false);

  // Deep link (?c=) and default selection.
  useEffect(() => {
    const c = params.get('c');
    if (c) setActiveId(c);
  }, [params]);
  useEffect(() => {
    if (!activeId && channels.length > 0) setActiveId(channels[0].id);
  }, [channels, activeId]);

  const active: Channel | undefined = useMemo(
    () => [...channels, ...dms].find(c => c.id === activeId),
    [channels, dms, activeId],
  );

  const dmTitle = (c: Channel) => person(c.members.find(m => m.id !== meId)?.id ?? c.members[0]?.id ?? '');

  return (
    <div className="flex h-full min-h-0">
      {/* Inner sidebar */}
      <aside className="w-64 shrink-0 border-e border-[var(--border)] flex flex-col bg-[var(--surface)]/30 min-h-0">
        <div className="h-14 px-4 flex items-center justify-between border-b border-[var(--border)] shrink-0">
          <span className="font-bold text-[var(--foreground)]">{t('channels')}</span>
        </div>
        <div className="flex-1 overflow-y-auto py-3">
          {/* Channels group */}
          <Group
            label={t('channels')}
            action={<button onClick={() => setNewChannelOpen(true)} title={t('newChannel')} className="text-[var(--muted)] hover:text-[var(--foreground)]"><Icon.plus size={15} /></button>}
          >
            {channels.map(c => (
              <Row key={c.id} active={c.id === activeId} onClick={() => setActiveId(c.id)}
                icon={<span className="text-[var(--muted)]">#</span>} label={c.name} />
            ))}
            {!loading && channels.length === 0 && <div className="px-3 py-1 text-xs text-[var(--muted)]">—</div>}
          </Group>

          {/* Direct messages group */}
          <Group
            label={t('directMessages')}
            action={<button onClick={() => setDmOpen(true)} title={t('startDm')} className="text-[var(--muted)] hover:text-[var(--foreground)]"><Icon.plus size={15} /></button>}
          >
            {dms.map(c => {
              const p = dmTitle(c);
              return (
                <Row key={c.id} active={c.id === activeId} onClick={() => setActiveId(c.id)}
                  icon={<Avatar person={p} size={18} />} label={p.name} />
              );
            })}
            {dms.length === 0 && <div className="px-3 py-1 text-xs text-[var(--muted)]">—</div>}
          </Group>
        </div>
      </aside>

      {/* Active pane */}
      <main className="flex-1 min-w-0 min-h-0">
        {active
          ? <ChannelView channel={active} onAddMember={() => setAddMemberOpen(true)} />
          : <div className="h-full flex items-center justify-center">
              <EmptyState icon={<Icon.send size={28} />} title={t('channels')} description={t('noChannelSelected')} />
            </div>}
      </main>

      {/* Modals */}
      <NewChannelModal open={newChannelOpen} onClose={() => setNewChannelOpen(false)}
        onCreate={async (name, desc) => { const c = await createChannel(name, desc); if (c) setActiveId(c.id); setNewChannelOpen(false); }} />

      <MemberPickerModal open={dmOpen} onClose={() => setDmOpen(false)} title={t('startDm')}
        people={Object.values(peopleById).filter(p => p.id !== meId)}
        onPick={async p => { const c = await openDm(p.id, p.kind === 'Agent' ? 'agent' : 'human'); if (c) setActiveId(c.id); setDmOpen(false); }} />

      {active && (
        <MemberPickerModal open={addMemberOpen} onClose={() => setAddMemberOpen(false)} title={t('addMember')}
          people={Object.values(peopleById).filter(p => p.id !== meId && !active.members.some(m => m.id === p.id))}
          onPick={async p => { await addMember(active.id, p.id, p.kind === 'Agent' ? 'agent' : 'human'); setAddMemberOpen(false); }} />
      )}
    </div>
  );
}

function Group({ label, action, children }: { label: string; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="mb-4">
      <div className="px-4 mb-1 flex items-center justify-between">
        <span className="text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)]">{label}</span>
        {action}
      </div>
      <div className="px-2 flex flex-col gap-0.5">{children}</div>
    </div>
  );
}

function Row({ active, onClick, icon, label }: { active: boolean; onClick: () => void; icon: React.ReactNode; label: string }) {
  return (
    <button onClick={onClick}
      className={`w-full flex items-center gap-2 px-2 py-1.5 rounded-md text-sm text-start truncate transition-colors
        ${active ? 'bg-[var(--primary)]/15 text-[var(--foreground)] font-medium' : 'text-[var(--muted)] hover:bg-[var(--background)] hover:text-[var(--foreground)]'}`}>
      <span className="shrink-0 w-5 flex justify-center">{icon}</span>
      <span className="truncate">{label}</span>
    </button>
  );
}

function NewChannelModal({ open, onClose, onCreate }: { open: boolean; onClose: () => void; onCreate: (name: string, desc?: string) => void }) {
  const { t } = useSettings();
  const [name, setName] = useState('');
  const [desc, setDesc] = useState('');
  useEffect(() => { if (open) { setName(''); setDesc(''); } }, [open]);
  return (
    <Modal open={open} onClose={onClose} title={t('newChannel')} width={440}
      footer={<><Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button variant="primary" onClick={() => name.trim() && onCreate(name.trim(), desc.trim() || undefined)}>{t('newChannel')}</Button></>}>
      <div className="flex flex-col gap-3">
        <input autoFocus value={name} onChange={e => setName(e.target.value)} placeholder="name"
          className="w-full bg-[var(--surface)] border border-[var(--border)] rounded-md px-3 h-10 text-sm text-[var(--foreground)] outline-none" />
        <textarea value={desc} onChange={e => setDesc(e.target.value)} placeholder="description" rows={2}
          className="w-full bg-[var(--surface)] border border-[var(--border)] rounded-md px-3 py-2 text-sm text-[var(--foreground)] outline-none resize-none" />
      </div>
    </Modal>
  );
}

function MemberPickerModal({ open, onClose, title, people, onPick }: {
  open: boolean; onClose: () => void; title: string; people: Person[]; onPick: (p: Person) => void;
}) {
  const [q, setQ] = useState('');
  useEffect(() => { if (open) setQ(''); }, [open]);
  const filtered = people.filter(p => p.name.toLowerCase().includes(q.toLowerCase()));
  return (
    <Modal open={open} onClose={onClose} title={title} width={420}>
      <div className="flex items-center gap-1.5 bg-[var(--surface)] rounded-md px-2.5 h-9 border border-[var(--border)] mb-2">
        <Icon.search size={15} className="text-[var(--muted)]" />
        <input autoFocus value={q} onChange={e => setQ(e.target.value)} placeholder="Search people & agents"
          className="bg-transparent outline-none text-sm flex-1 text-[var(--foreground)]" />
      </div>
      <div className="max-h-72 overflow-y-auto flex flex-col gap-0.5">
        {filtered.map(p => (
          <button key={p.id} onClick={() => onPick(p)}
            className="w-full flex items-center gap-2.5 px-2 py-2 rounded-md hover:bg-[var(--background)] text-start">
            <Avatar person={p} size={28} />
            <span className="text-sm text-[var(--foreground)]">{p.name}</span>
            {p.kind === 'Agent' && <span className="text-[10px] px-1.5 py-px rounded bg-[var(--primary)]/15 text-[var(--primary)] font-medium ms-auto">AGENT</span>}
          </button>
        ))}
        {filtered.length === 0 && <div className="text-sm text-[var(--muted)] px-2 py-3">No matches</div>}
      </div>
    </Modal>
  );
}
