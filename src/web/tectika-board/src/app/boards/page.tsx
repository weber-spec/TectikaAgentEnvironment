'use client';

import { Suspense, useEffect, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { api } from '@/lib/api';
import { useSettings } from '@/lib/settings-context';
import type { Board, AgentTask } from '@/lib/types';
import { STATUS_CONFIG, colorFor } from '@/lib/palette';
import { Button, Skeleton, EmptyState, Avatar } from '@/components/ui/primitives';
import { Modal } from '@/components/ui/overlays';
import { Icon } from '@/components/ui/icons';
import { relativeTime, displayName } from '@/lib/format';
import { toast } from '@/lib/toast';

interface BoardSummary { board: Board; tasks: AgentTask[] }

export default function BoardsPage() {
  return <Suspense><BoardsPageContent /></Suspense>;
}

function BoardsPageContent() {
  const { t } = useSettings();
  const searchParams = useSearchParams();
  const [summaries, setSummaries] = useState<BoardSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [q, setQ] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDesc, setNewDesc] = useState('');
  const [saving, setSaving] = useState(false);

  const load = () => {
    api.boards.list().then(async boards => {
      const withTasks = await Promise.all(boards.map(async b => ({ board: b, tasks: await api.tasks.list(b.id).catch(() => []) })));
      setSummaries(withTasks);
    }).catch(() => setSummaries([])).finally(() => setLoading(false));
  };
  useEffect(load, []);

  useEffect(() => { if (searchParams.get('new') === '1') { setNewName(''); setNewDesc(''); setModalOpen(true); } }, [searchParams]);

  const openModal = () => { setNewName(''); setNewDesc(''); setModalOpen(true); };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newName.trim()) return;
    setSaving(true);
    try {
      const b = await api.boards.create(newName.trim(), newDesc.trim() || undefined);
      setSummaries(prev => [...prev, { board: b, tasks: [] }]);
      setModalOpen(false);
      toast('Board created', 'success');
    } catch { toast('Could not create board', 'error'); }
    finally { setSaving(false); }
  };

  const filtered = summaries.filter(s => s.board.name.toLowerCase().includes(q.toLowerCase()));

  return (
    <div className="flex flex-col h-full overflow-auto">
      <div className="px-8 py-5 flex items-center justify-between flex-wrap gap-3">
        <div>
          <h1 className="text-2xl font-bold text-[var(--foreground)]">{t('boards')}</h1>
          <p className="text-sm text-[var(--muted)] mt-0.5">Orchestrate AI agents and humans across your workspaces.</p>
        </div>
        <div className="flex items-center gap-2">
          <div className="flex items-center gap-1.5 bg-[var(--surface)] rounded-md px-2.5 h-9 border border-[var(--border)]">
            <Icon.search size={15} className="text-[var(--muted)]" />
            <input value={q} onChange={e => setQ(e.target.value)} placeholder="Search boards" className="bg-transparent outline-none text-sm w-40 text-[var(--foreground)]" />
          </div>
          <Button variant="primary" onClick={openModal}><Icon.plus size={16} /> {t('newBoard')}</Button>
        </div>
      </div>

      <div className="px-8 pb-8 flex-1">
        {loading ? (
          <div className="grid gap-4" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))' }}>
            {[...Array(3)].map((_, i) => <Skeleton key={i} className="h-44" />)}
          </div>
        ) : filtered.length === 0 ? (
          <EmptyState icon={<Icon.board size={48} />} title={t('noBoardsYet')} description={t('noBoardsDesc')}
            action={<Button variant="primary" onClick={openModal}><Icon.plus size={16} /> {t('newBoard')}</Button>} />
        ) : (
          <div className="grid gap-4" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))' }}>
            {filtered.map(s => <BoardCard key={s.board.id} summary={s} />)}
          </div>
        )}
      </div>

      <Modal
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        title="New board"
        width={480}
        footer={
          <>
            <Button variant="ghost" onClick={() => setModalOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleCreate} disabled={!newName.trim() || saving}>
              {saving ? 'Creating…' : 'Create board'}
            </Button>
          </>
        }
      >
        <form onSubmit={handleCreate} className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-[var(--muted)]">Board name <span className="text-[#e2445c]">*</span></label>
            <input
              autoFocus
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="e.g. Checkout Service"
              className="w-full h-9 rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)] transition-colors"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <label className="text-xs font-medium text-[var(--muted)]">Description</label>
            <textarea
              value={newDesc}
              onChange={e => setNewDesc(e.target.value)}
              placeholder="What is this board for? Agents will use this as project context."
              rows={3}
              className="w-full rounded-md border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--foreground)] outline-none focus:border-[var(--primary)] transition-colors resize-none"
            />
          </div>
        </form>
      </Modal>
    </div>
  );
}

function BoardCard({ summary }: { summary: BoardSummary }) {
  const { board, tasks } = summary;
  const color = colorFor(board.name);
  const counts = new Map<string, number>();
  tasks.forEach(t => counts.set(t.status, (counts.get(t.status) ?? 0) + 1));
  const done = counts.get('Done') ?? 0;
  const pct = tasks.length ? Math.round((done / tasks.length) * 100) : 0;
  const owners = Array.from(new Set(tasks.map(t => t.assignee.id))).slice(0, 4);

  return (
    <Link href={`/boards/${board.id}`} className="group block bg-[var(--background)] rounded-xl border border-[var(--border)] overflow-hidden hover:shadow-lg hover:border-[var(--primary)] transition-all">
      <div className="h-1.5" style={{ background: color }} />
      <div className="p-4">
        <div className="flex items-start justify-between gap-2 mb-1">
          <h3 className="font-semibold text-[var(--foreground)] group-hover:text-[var(--primary)] transition-colors">{board.name}</h3>
          <Icon.flow size={16} className="text-[var(--muted-2)] group-hover:text-[var(--primary)]" />
        </div>
        <p className="text-xs text-[var(--muted)] line-clamp-2 mb-3 min-h-[32px]">{board.description || 'No description'}</p>

        {/* battery */}
        <div className="flex h-2 rounded-full overflow-hidden bg-[var(--surface-2)] mb-2">
          {tasks.length === 0 ? <div className="flex-1" /> : Array.from(counts.entries()).map(([st, n]) => (
            <div key={st} style={{ flex: n, background: STATUS_CONFIG[st as keyof typeof STATUS_CONFIG]?.hex }} title={`${STATUS_CONFIG[st as keyof typeof STATUS_CONFIG]?.label}: ${n}`} />
          ))}
        </div>
        <div className="flex items-center justify-between text-[11px] text-[var(--muted)]">
          <span>{tasks.length} items · {pct}% done</span>
          <span>{relativeTime(board.createdAt)}</span>
        </div>

        <div className="flex items-center justify-between mt-3 pt-3 border-t border-[var(--border)]">
          <div className="flex items-center">
            {owners.map((o, i) => <div key={o} style={{ marginLeft: i === 0 ? 0 : -8, zIndex: 10 - i }}><Avatar name={displayName(o)} hex={colorFor(o)} size={24} ring /></div>)}
          </div>
          <span className="text-[11px] text-[var(--primary)] font-medium opacity-0 group-hover:opacity-100 transition-opacity">Open →</span>
        </div>
      </div>
    </Link>
  );
}
