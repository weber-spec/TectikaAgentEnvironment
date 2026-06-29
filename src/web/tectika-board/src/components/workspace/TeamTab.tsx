'use client';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AgentTask, Comment, CommentKind, NoteType, Person } from '@/lib/types';
import { api } from '@/lib/api';
import { useBoard } from '@/lib/board-context';
import { CURRENT_USER } from '@/lib/collaboration';
import { Avatar, Button } from '@/components/ui/primitives';
import { Markdown } from '@/lib/markdown';
import { relativeTime } from '@/lib/format';
import { parseMentions, splitComments } from '@/lib/team-notes';

const NOTE_TYPE_META: Record<NoteType, { label: string; hex: string }> = {
  decision: { label: 'Decision', hex: '#f0a020' },
  open_question: { label: 'Open Q', hex: '#e2445c' },
  note: { label: 'Note', hex: '#676879' },
};
const REACTIONS = ['👍', '🎉', '👀', '🙏'];

export function TeamTab({ task }: { task: AgentTask }) {
  const { peopleById, people } = useBoard();
  const me = CURRENT_USER.id;
  const [comments, setComments] = useState<Comment[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const seq = useRef(0);

  const load = useCallback(async () => {
    const id = ++seq.current;
    try {
      const list = await api.comments.list(task.boardId, task.id);
      if (id === seq.current) { setComments(list); setLoaded(true); }
    } catch { /* keep last good state; polling retries */ }
  }, [task.boardId, task.id]);

  useEffect(() => {
    load();
    api.comments.markRead(task.boardId, task.id).catch(() => {});
    const poll = setInterval(() => {
      if (document.visibilityState === 'hidden') return;
      load();
    }, 4000);
    return () => clearInterval(poll);
  }, [load, task.boardId, task.id]);

  const { notes, messages } = useMemo(() => splitComments(comments), [comments]);

  const post = async (kind: CommentKind, body: string, noteType?: NoteType) => {
    const text = body.trim();
    if (!text || sending) return;
    setSending(true);
    const mentions = parseMentions(text, people);
    const optimistic: Comment = {
      id: `tmp-${Date.now()}`, taskId: task.id, boardId: task.boardId, kind,
      noteType, authorId: me, body: text, mentions, reactions: {}, createdAt: new Date().toISOString(),
    };
    setComments(prev => [...prev, optimistic]);
    if (kind === 'message') setDraft('');
    try {
      const saved = await api.comments.create(task.boardId, task.id, { kind, noteType, body: text, mentions });
      setComments(prev => prev.map(c => (c.id === optimistic.id ? saved : c)));
    } catch {
      setComments(prev => prev.filter(c => c.id !== optimistic.id));
      if (kind === 'message') setDraft(text);
    } finally {
      setSending(false);
    }
  };

  const toggleReaction = async (c: Comment, emoji: string) => {
    try { const updated = await api.comments.react(task.boardId, task.id, c.id, emoji); setComments(prev => prev.map(x => x.id === c.id ? updated : x)); }
    catch { /* polling reconciles */ }
  };
  const toggleShare = async (c: Comment) => {
    try { const updated = await api.comments.share(task.boardId, task.id, c.id, !c.sharedWithAgent); setComments(prev => prev.map(x => x.id === c.id ? updated : x)); }
    catch { /* ignore */ }
  };
  const remove = async (c: Comment) => {
    if (!confirm('Delete this?')) return;
    setComments(prev => prev.map(x => x.id === c.id ? { ...x, deletedAt: new Date().toISOString() } : x));
    try { await api.comments.remove(task.boardId, task.id, c.id); } catch { load(); }
  };
  const edit = useCallback(async (c: Comment, body: string, noteType?: NoteType) => {
    const text = body.trim();
    if (!text) return;
    setComments(p => p.map(x => x.id === c.id
      ? { ...x, body: text, noteType: noteType ?? x.noteType, updatedAt: new Date().toISOString(), editedBy: me }
      : x));
    try {
      const saved = await api.comments.update(task.boardId, task.id, c.id, { body: text, noteType });
      setComments(p => p.map(x => x.id === c.id ? saved : x));
    } catch {
      setComments(p => p.map(x => x.id === c.id ? c : x)); // surgical rollback to the original comment
    }
  }, [task.boardId, task.id, me]);

  if (!loaded) return <div className="p-4 text-[13px] text-[var(--muted)]">Loading…</div>;

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 overflow-auto">
        <section className="p-3.5 border-b border-[var(--border)]">
          <div className="flex items-center justify-between mb-2">
            <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">📌 Notes</span>
            <AddNote onAdd={(body, noteType) => post('note', body, noteType)} />
          </div>
          {notes.length === 0
            ? <p className="text-[12px] text-[var(--muted)]">No notes yet — capture a decision or open question.</p>
            : <div className="flex flex-col gap-2">{notes.map(n =>
                <NoteCard key={n.id} note={n} me={me} authorName={peopleById[n.authorId]?.name ?? n.authorId}
                  onShare={() => toggleShare(n)} onEdit={(body, noteType) => edit(n, body, noteType)} onDelete={() => remove(n)} />)}</div>}
        </section>

        <section className="p-3.5 flex flex-col gap-3.5">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">💬 Discussion</span>
          {messages.length === 0
            ? <p className="text-[12px] text-[var(--muted)]">No messages yet.</p>
            : messages.map(m =>
                <MessageRow key={m.id} comment={m} me={me} person={peopleById[m.authorId]}
                  reactions={REACTIONS} onReact={(e) => toggleReaction(m, e)} onEdit={(body) => edit(m, body)} onDelete={() => remove(m)} />)}
        </section>
      </div>

      <div className="border-t border-[var(--border)] p-3.5 flex gap-2 items-end">
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) post('message', draft); }}
          placeholder="Message the team…  @ to mention"
          rows={2}
          className="flex-1 bg-[var(--surface)] border border-[var(--border)] rounded-lg p-2 text-[13px] outline-none resize-none focus:border-[var(--primary)]"
        />
        <Button variant="primary" size="sm" disabled={sending || !draft.trim()} onClick={() => post('message', draft)}>Send</Button>
      </div>
    </div>
  );
}

function NoteTypeChips({ value, onChange }: { value: NoteType; onChange: (t: NoteType) => void }) {
  return (
    <div className="flex gap-1.5 mb-1.5">
      {(['decision', 'open_question', 'note'] as NoteType[]).map(t => (
        <button key={t} onClick={() => onChange(t)}
          className={`text-[10px] px-2 py-0.5 rounded ${value === t ? 'text-white' : 'text-[var(--muted)] border border-[var(--border)]'}`}
          style={value === t ? { background: NOTE_TYPE_META[t].hex } : undefined}>{NOTE_TYPE_META[t].label}</button>
      ))}
    </div>
  );
}

function AddNote({ onAdd }: { onAdd: (body: string, noteType: NoteType) => void }) {
  const [open, setOpen] = useState(false);
  const [body, setBody] = useState('');
  const [noteType, setNoteType] = useState<NoteType>('note');
  if (!open) return <button className="text-[11px] text-[var(--primary)]" onClick={() => setOpen(true)}>+ Add note</button>;
  return (
    <div className="w-full mt-1">
      <NoteTypeChips value={noteType} onChange={setNoteType} />
      <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={2} autoFocus
        className="w-full bg-[var(--surface)] border border-[var(--border)] rounded-lg p-2 text-[13px] outline-none resize-none focus:border-[var(--primary)]" />
      <div className="flex gap-2 mt-1.5">
        <Button variant="primary" size="sm" disabled={!body.trim()} onClick={() => { onAdd(body, noteType); setBody(''); setOpen(false); }}>Save note</Button>
        <Button variant="ghost" size="sm" onClick={() => { setBody(''); setOpen(false); }}>Cancel</Button>
      </div>
    </div>
  );
}

function NoteCard({ note, me, authorName, onShare, onEdit, onDelete }: {
  note: Comment; me: string; authorName: string;
  onShare: () => void; onEdit: (body: string, noteType: NoteType) => void; onDelete: () => void;
}) {
  const meta = NOTE_TYPE_META[note.noteType ?? 'note'];
  const [editing, setEditing] = useState(false);
  const [body, setBody] = useState(note.body);
  const [noteType, setNoteType] = useState<NoteType>(note.noteType ?? 'note');
  const isMine = note.authorId === me;

  if (editing) {
    return (
      <div className="rounded-lg p-2.5" style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <NoteTypeChips value={noteType} onChange={setNoteType} />
        <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={2} autoFocus
          className="w-full bg-[var(--surface)] border border-[var(--border)] rounded-lg p-2 text-[13px] outline-none resize-none focus:border-[var(--primary)]" />
        <div className="flex gap-2 mt-1.5">
          <Button variant="primary" size="sm" disabled={!body.trim()} onClick={() => { onEdit(body, noteType); setEditing(false); }}>Save</Button>
          <Button variant="ghost" size="sm" onClick={() => { setBody(note.body); setNoteType(note.noteType ?? 'note'); setEditing(false); }}>Cancel</Button>
        </div>
      </div>
    );
  }

  return (
    <div className="rounded-lg p-2.5" style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
      <div className="flex items-center justify-between mb-1">
        <span className="text-[9px] uppercase tracking-wide px-1.5 py-0.5 rounded text-white" style={{ background: meta.hex }}>{meta.label}</span>
        <button onClick={onShare} className="text-[11px] font-semibold" style={{ color: note.sharedWithAgent ? '#6b4ce6' : 'var(--muted)' }}>
          {note.sharedWithAgent ? '✓ Shared with agent' : '⤳ Share with agent'}
        </button>
      </div>
      <div className="text-[12px]"><Markdown text={note.body} /></div>
      <div className="text-[10px] text-[var(--muted)] mt-1.5 flex gap-2">
        <span>{note.editedBy ? `edited by ${note.editedBy}` : authorName} · {relativeTime(note.updatedAt ?? note.createdAt)}</span>
        {isMine && <button onClick={() => { setBody(note.body); setNoteType(note.noteType ?? 'note'); setEditing(true); }} className="hover:text-[var(--foreground)]">edit</button>}
        {isMine && <button onClick={onDelete} className="hover:text-[var(--foreground)]">delete</button>}
      </div>
    </div>
  );
}

function MessageRow({ comment, me, person, reactions, onReact, onEdit, onDelete }: {
  comment: Comment; me: string; person?: Person; reactions: string[];
  onReact: (emoji: string) => void; onEdit: (body: string) => void; onDelete: () => void;
}) {
  const [hover, setHover] = useState(false);
  const [editing, setEditing] = useState(false);
  const [body, setBody] = useState(comment.body);
  const isMine = comment.authorId === me;
  if (comment.deletedAt) return <div className="text-[12px] text-[var(--muted)] italic pl-9">message deleted</div>;
  return (
    <div className="flex gap-2.5" onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}>
      <Avatar person={person} name={person?.name ?? comment.authorId} hex={person?.hex} size={26} />
      <div className="flex-1 min-w-0">
        <div className="text-[12px]">
          <strong>{person?.name ?? comment.authorId}</strong>{' '}
          <span className="text-[var(--muted)]">{relativeTime(comment.createdAt)}</span>
          {hover && !editing && (
            <span className="ml-2">
              {reactions.map(e => <button key={e} className="opacity-60 hover:opacity-100" onClick={() => onReact(e)}>{e}</button>)}
              {isMine && <button className="ml-1 text-[var(--muted)] hover:text-[var(--foreground)]" onClick={() => { setBody(comment.body); setEditing(true); }}>edit</button>}
              {isMine && <button className="ml-1 text-[var(--muted)] hover:text-[var(--foreground)]" onClick={onDelete}>✕</button>}
            </span>
          )}
        </div>
        {editing ? (
          <div className="mt-1">
            <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={2} autoFocus
              className="w-full bg-[var(--surface)] border border-[var(--border)] rounded-lg p-2 text-[13px] outline-none resize-none focus:border-[var(--primary)]" />
            <div className="flex gap-2 mt-1.5">
              <Button variant="primary" size="sm" disabled={!body.trim()} onClick={() => { onEdit(body); setEditing(false); }}>Save</Button>
              <Button variant="ghost" size="sm" onClick={() => { setBody(comment.body); setEditing(false); }}>Cancel</Button>
            </div>
          </div>
        ) : (
          <div className="text-[12px]"><Markdown text={comment.body} /></div>
        )}
        {comment.reactions && Object.keys(comment.reactions).length > 0 && (
          <div className="flex gap-1.5 mt-1">
            {Object.entries(comment.reactions).map(([emoji, users]) => (
              <button key={emoji} onClick={() => onReact(emoji)}
                className="text-[11px] px-1.5 rounded-full border border-[var(--border)] bg-[var(--background)]">{emoji} {users.length}</button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
