'use client';

// Channels feature state — the channel/DM list, the active channel, the people/agent roster for
// avatars + the @mention picker, and the create/DM/add-member actions. Message transcript state
// lives in the message pane (poll + SSE), like the board's per-run event stream.

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { api } from './api';
import type { AgentRole, Channel, Person } from './types';
import { buildPeople, CURRENT_USER } from './collaboration';
import { colorFor } from './palette';
import { displayName } from './format';

export interface ChannelsContextValue {
  channels: Channel[];
  dms: Channel[];
  loading: boolean;
  peopleById: Record<string, Person>;
  /** The current signed-in user id (Entra id / email). */
  meId: string;
  reload: () => void;
  createChannel: (name: string, description?: string, memberIds?: string[]) => Promise<Channel | null>;
  openDm: (otherId: string, otherType?: 'human' | 'agent') => Promise<Channel | null>;
  addMember: (channelId: string, memberId: string, memberType?: 'human' | 'agent') => Promise<void>;
  removeMember: (channelId: string, memberId: string) => Promise<void>;
  /** Resolve a member/author id to a display Person (falls back to a derived name+color). */
  person: (id: string) => Person;
}

const ChannelsContext = createContext<ChannelsContextValue | null>(null);

export function ChannelsProvider({ children }: { children: React.ReactNode }) {
  const [all, setAll] = useState<Channel[]>([]);
  const [roles, setRoles] = useState<AgentRole[]>([]);
  const [loading, setLoading] = useState(true);

  const meId = CURRENT_USER.id;

  const reload = useCallback(() => {
    api.channels.list()
      .then(setAll)
      .catch(() => setAll([]))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { reload(); }, [reload]);
  useEffect(() => { api.agentRoles.list().then(setRoles).catch(() => setRoles([])); }, []);

  const peopleById = useMemo(() => buildPeople(roles, []), [roles]);

  const person = useCallback((id: string): Person => {
    if (peopleById[id]) return peopleById[id];
    if (id === 'system') return { id, name: 'System', kind: 'Human', hex: '#8b8f98' };
    return { id, name: displayName(id), kind: id.includes('@') ? 'Human' : 'Agent', hex: colorFor(id) };
  }, [peopleById]);

  const createChannel = useCallback(async (name: string, description?: string, memberIds?: string[]) => {
    try {
      const ch = await api.channels.create({ name, description, memberIds });
      setAll(prev => [...prev, ch]);
      return ch;
    } catch { return null; }
  }, []);

  const openDm = useCallback(async (otherId: string, otherType?: 'human' | 'agent') => {
    try {
      const dm = await api.channels.dm(otherId, otherType);
      setAll(prev => prev.some(c => c.id === dm.id) ? prev : [...prev, dm]);
      return dm;
    } catch { return null; }
  }, []);

  const addMember = useCallback(async (channelId: string, memberId: string, memberType?: 'human' | 'agent') => {
    try {
      const ch = await api.channels.addMember(channelId, memberId, memberType);
      setAll(prev => prev.map(c => c.id === ch.id ? ch : c));
    } catch { /* ignore */ }
  }, []);

  const removeMember = useCallback(async (channelId: string, memberId: string) => {
    try {
      const ch = await api.channels.removeMember(channelId, memberId);
      setAll(prev => prev.map(c => c.id === ch.id ? ch : c));
    } catch { /* ignore */ }
  }, []);

  const channels = useMemo(() => all.filter(c => c.type === 'channel'), [all]);
  const dms = useMemo(() => all.filter(c => c.type === 'dm'), [all]);

  const value: ChannelsContextValue = {
    channels, dms, loading, peopleById, meId,
    reload, createChannel, openDm, addMember, removeMember, person,
  };
  return <ChannelsContext.Provider value={value}>{children}</ChannelsContext.Provider>;
}

export function useChannels(): ChannelsContextValue {
  const ctx = useContext(ChannelsContext);
  if (!ctx) throw new Error('useChannels must be used within a ChannelsProvider');
  return ctx;
}
