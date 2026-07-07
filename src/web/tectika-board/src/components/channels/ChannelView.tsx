'use client';

// The message pane for one channel/DM: header + members, the transcript (poll + SSE), and a composer
// with an @mention picker. Agent replies arrive as mirrored `agent_message` / `artifact` messages.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '@/lib/api';
import { useSettings } from '@/lib/settings-context';
import { useChannels } from '@/lib/channels-context';
import type { Channel, ChannelMessage, Person } from '@/lib/types';
import { Avatar, Button } from '@/components/ui/primitives';
import { Icon } from '@/components/ui/icons';
import { relativeTime } from '@/lib/format';

const REACTIONS = ['👍', '🎉', '👀', '🙏'];

export function ChannelView({ channel, onAddMember }: { channel: Channel; onAddMember: () => void }) {
  const { t } = useSettings();
  const { person, meId } = useChannels();
  const [messages, setMessages] = useState<ChannelMessage[]>([]);
  const [draft, setDraft] = useState('');
  const [mentionIds, setMentionIds] = useState<Set<string>>(new Set());
  const [picker, setPicker] = useState<{ query: string } | null>(null);
  const [sending, setSending] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  const taRef = useRef<HTMLTextAreaElement>(null);

  const title = channel.type === 'dm'
    ? person(channel.members.find(m => m.id !== meId)?.id ?? channel.members[0]?.id ?? '').name
    : `# ${channel.name}`;

  // Merge helper — keep oldest-first, dedupe by id (SSE + poll can both deliver the same message).
  const merge = useCallback((incoming: ChannelMessage[]) => {
    setMessages(prev => {
      const byId = new Map(prev.map(m => [m.id, m]));
      for (const m of incoming) byId.set(m.id, m);
      return [...byId.values()].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    });
  }, []);

  // Load + poll (the poll also drives server-side reconcile of agent replies) + live SSE.
  useEffect(() => {
    let alive = true;
    setMessages([]);
    api.channels.messages(channel.id).then(m => { if (alive) merge(m); }).catch(() => {});
    api.channels.markRead(channel.id).catch(() => {});

    const poll = setInterval(() => {
      if (document.visibilityState === 'hidden') return;
      api.channels.messages(channel.id).then(m => { if (alive) merge(m); }).catch(() => {});
    }, 3500);

    const stop = api.streamChannel(channel.id, msg => { if (alive) merge([msg]); });

    return () => { alive = false; clearInterval(poll); stop(); };
  }, [channel.id, merge]);

  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [messages.length]);

  const mentionCandidates: Person[] = useMemo(() => {
    const q = (picker?.query ?? '').toLowerCase();
    return channel.members
      .map(m => person(m.id))
      .filter(p => p.id !== meId && p.name.toLowerCase().includes(q));
  }, [channel.members, picker, person, meId]);

  const onDraftChange = (v: string) => {
    setDraft(v);
    const m = /(?:^|\s)@([\p{L}\w.-]*)$/u.exec(v);
    setPicker(m ? { query: m[1] } : null);
  };

  const pickMention = (p: Person) => {
    setDraft(d => d.replace(/(?:^|\s)@([\p{L}\w.-]*)$/u, (full, _tok, off) => (off === 0 ? '' : ' ') + `@${p.name} `));
    setMentionIds(prev => new Set(prev).add(p.id));
    setPicker(null);
    taRef.current?.focus();
  };

  const send = async () => {
    const body = draft.trim();
    if (!body || sending) return;
    setSending(true);
    // Resolve mentions: any tracked id whose name still appears in the text.
    const mentions = [...mentionIds].filter(id => draft.includes(`@${person(id).name}`));
    try {
      const created = await api.channels.postMessage(channel.id, body, mentions);
      merge([created]);
      setDraft('');
      setMentionIds(new Set());
      setPicker(null);
    } catch { /* keep draft on failure */ }
    finally { setSending(false); }
  };

  const react = async (messageId: string, emoji: string) => {
    try { merge([await api.channels.react(channel.id, messageId, emoji)]); } catch { /* ignore */ }
  };

  return (
    <div className="flex flex-col h-full min-h-0">
      {/* Header */}
      <div className="h-14 px-5 flex items-center justify-between border-b border-[var(--border)] shrink-0">
        <div className="min-w-0">
          <div className="font-semibold text-[var(--foreground)] truncate">{title}</div>
          {channel.description && <div className="text-xs text-[var(--muted)] truncate">{channel.description}</div>}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <div className="flex items-center">
            {channel.members.slice(0, 5).map((m, i) => (
              <div key={m.id} style={{ marginLeft: i === 0 ? 0 : -8 }}>
                <Avatar person={person(m.id)} size={24} ring />
              </div>
            ))}
          </div>
          {channel.type !== 'dm' && (
            <Button variant="secondary" size="sm" onClick={onAddMember}>
              <Icon.plus size={14} /> {t('addMember')}
            </Button>
          )}
        </div>
      </div>

      {/* Transcript */}
      <div className="flex-1 min-h-0 overflow-y-auto px-5 py-4 flex flex-col gap-3">
        {messages.length === 0 && (
          <div className="m-auto text-sm text-[var(--muted)]">{t('noMessagesYet')}</div>
        )}
        {messages.map(m => <MessageRow key={m.id} m={m} author={person(m.authorId)} onReact={react} />)}
        <div ref={endRef} />
      </div>

      {/* Composer */}
      <div className="px-5 pb-4 pt-2 border-t border-[var(--border)] shrink-0 relative">
        {picker && mentionCandidates.length > 0 && (
          <div className="absolute bottom-full mb-1 left-5 right-5 max-h-52 overflow-y-auto bg-[var(--surface)] border border-[var(--border)] rounded-lg shadow-lg z-10">
            {mentionCandidates.map(p => (
              <button key={p.id} onClick={() => pickMention(p)}
                className="w-full flex items-center gap-2 px-3 py-2 hover:bg-[var(--background)] text-start">
                <Avatar person={p} size={22} />
                <span className="text-sm text-[var(--foreground)]">{p.name}</span>
                <span className="text-xs text-[var(--muted)] ms-auto">{p.kind === 'Agent' ? 'Agent' : ''}</span>
              </button>
            ))}
          </div>
        )}
        <div className="flex items-end gap-2 bg-[var(--surface)] border border-[var(--border)] rounded-lg px-3 py-2">
          <textarea
            ref={taRef}
            value={draft}
            onChange={e => onDraftChange(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } }}
            rows={1}
            placeholder={`${t('messagePlaceholder')} ${title}`}
            className="flex-1 bg-transparent outline-none resize-none text-sm text-[var(--foreground)] max-h-40"
          />
          <button onClick={send} disabled={!draft.trim() || sending}
            className="text-[var(--primary)] disabled:opacity-40 shrink-0 p-1">
            <Icon.send size={18} />
          </button>
        </div>
      </div>
    </div>
  );
}

function MessageRow({ m, author, onReact }: { m: ChannelMessage; author: Person; onReact: (id: string, emoji: string) => void }) {
  const [hover, setHover] = useState(false);
  if (m.kind === 'system') {
    return <div className="text-center text-xs text-[var(--muted)] py-1">{m.body}</div>;
  }
  const isArtifact = m.kind === 'artifact';
  return (
    <div className="flex gap-3 group" onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}>
      <Avatar person={author} size={34} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-semibold text-sm text-[var(--foreground)]">{author.name}</span>
          {author.kind === 'Agent' && <span className="text-[10px] px-1.5 py-px rounded bg-[var(--primary)]/15 text-[var(--primary)] font-medium">AGENT</span>}
          <span className="text-xs text-[var(--muted)]">{relativeTime(m.createdAt)}</span>
        </div>
        {isArtifact && (
          <div className="mt-1 inline-flex items-center gap-1.5 text-xs text-[var(--muted)]">
            <Icon.file size={13} /> Artifact
          </div>
        )}
        <div className={`text-sm whitespace-pre-wrap break-words mt-0.5 ${isArtifact ? 'border-s-2 border-[var(--primary)]/40 ps-3' : ''} text-[var(--foreground)]`}>
          {m.body}
        </div>
        {m.reactions && Object.keys(m.reactions).length > 0 && (
          <div className="flex gap-1 mt-1.5 flex-wrap">
            {Object.entries(m.reactions).map(([emoji, users]) => (
              <button key={emoji} onClick={() => onReact(m.id, emoji)}
                className="text-xs px-1.5 py-0.5 rounded-full border border-[var(--border)] bg-[var(--surface)] hover:border-[var(--primary)]">
                {emoji} {users.length}
              </button>
            ))}
          </div>
        )}
      </div>
      <div className={`shrink-0 flex items-start gap-0.5 ${hover ? 'opacity-100' : 'opacity-0'} transition-opacity`}>
        {REACTIONS.map(e => (
          <button key={e} onClick={() => onReact(m.id, e)} className="text-sm px-1 hover:scale-125 transition-transform" title={e}>{e}</button>
        ))}
      </div>
    </div>
  );
}
