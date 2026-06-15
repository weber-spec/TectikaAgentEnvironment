'use client';

import { useEffect, useState, useCallback } from 'react';
import { useSettings } from './settings-context';
import { api } from './api';
import type { AppNotification } from './types';
import type { Notification } from '@/components/layout/NotificationPanel';

const API_BASE = api.base;

// Maps settings keys to the sourceEventType values they cover
const PREF_MAP: Record<string, string[]> = {
  taskCompleted:    ['run_completed'],
  taskFailed:       ['run_failed'],
  approvalRequired: ['approval_required', 'interaction_required'],
  agentCreated:     ['agent_created'],
  agentDeleted:     ['agent_deleted'],
};

function toPanel(n: AppNotification, lastReadAt: Date): Notification {
  return {
    id: n.id,
    type: n.type as Notification['type'],
    title: n.title,
    subtitle: n.subtitle,
    taskId: n.taskId,
    boardId: n.boardId,
    timestamp: new Date(n.timestamp),
    read: new Date(n.timestamp) <= lastReadAt,
  };
}

export function useNotifications() {
  const { settings, updateNotification } = useSettings();
  const [raw, setRaw] = useState<AppNotification[]>([]);
  const [lastReadAt, setLastReadAt] = useState<Date>(new Date(0));

  const isEnabled = useCallback(
    (sourceEventType: string) =>
      Object.entries(PREF_MAP).some(
        ([key, types]) =>
          settings.notifications[key as keyof typeof settings.notifications] &&
          types.includes(sourceEventType),
      ),
    [settings.notifications],
  );

  useEffect(() => {
    let es: EventSource | null = null;

    async function init() {
      // 1. Load user settings from backend (preferences + lastReadAt)
      try {
        const res = await fetch(`${API_BASE}/api/settings/notifications`);
        if (res.ok) {
          const data = await res.json();
          setLastReadAt(new Date(data.notificationsLastReadAt ?? 0));
          // Merge backend prefs into local context
          const prefs = data.notifications as Record<string, boolean> | undefined;
          if (prefs) {
            Object.entries(prefs).forEach(([key, val]) => {
              updateNotification(key as keyof typeof settings.notifications, val);
            });
          }
        }
      } catch {
        // Backend unavailable — fall back to localStorage values
      }

      // 2. Load notification history
      try {
        const res = await fetch(`${API_BASE}/api/notifications?limit=50`);
        if (res.ok) {
          const data: AppNotification[] = await res.json();
          setRaw(data.filter(n => isEnabled(n.sourceEventType)));
        }
      } catch {
        // Backend unavailable — show empty list
      }

      // 3. Subscribe to live SSE stream
      es = new EventSource(`${API_BASE}/api/notifications/stream`);
      es.onmessage = (e) => {
        const n: AppNotification = JSON.parse(e.data as string);
        if (isEnabled(n.sourceEventType)) {
          setRaw(prev => [n, ...prev].slice(0, 50));
        }
      };
    }

    void init();
    return () => es?.close();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const markAllRead = useCallback(async () => {
    try {
      const res = await fetch(`${API_BASE}/api/notifications/mark-all-read`, { method: 'PATCH' });
      if (res.ok) {
        const data = await res.json() as { lastReadAt: string };
        setLastReadAt(new Date(data.lastReadAt));
      }
    } catch {
      setLastReadAt(new Date());
    }
  }, []);

  const notifications: Notification[] = raw.map(n => toPanel(n, lastReadAt));
  const unreadCount = notifications.filter(n => !n.read).length;

  return { notifications, unreadCount, markAllRead };
}
