'use client';

import { useEffect, useState, useCallback, useRef } from 'react';
import { useSettings } from './settings-context';
import { api } from './api';
import type { AppNotification } from './types';
import type { Notification } from '@/components/layout/NotificationPanel';

const API_BASE = api.base;
const LAST_READ_KEY = 'tectika-notifications-last-read';
const PANEL_LIMIT = 5;

// Maps settings keys to the sourceEventType values they cover
const PREF_MAP: Record<string, string[]> = {
  taskCompleted:    ['run_completed'],
  taskFailed:       ['run_failed'],
  approvalRequired: ['approval_required', 'interaction_required'],
  agentCreated:     ['agent_created'],
  agentDeleted:     ['agent_deleted'],
};

function readLastReadFromStorage(): Date {
  if (typeof window === 'undefined') return new Date();
  const stored = localStorage.getItem(LAST_READ_KEY);
  // If nothing stored yet, treat everything as already read (now)
  return stored ? new Date(stored) : new Date();
}

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
  // Initialise from localStorage immediately — no flash of wrong unread count
  const [lastReadAt, setLastReadAt] = useState<Date>(readLastReadFromStorage);
  const lastReadAtRef = useRef(lastReadAt);

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
    // The stream is opened after two awaits, so an unmount before that point (a fast route change, or
    // React StrictMode's double-invoke in dev) used to run the cleanup while `es` was still null — and the
    // connection that arrived a moment later was never closed. Leaked connections are exactly what
    // exhausts the browser's per-origin pool.
    let cancelled = false;

    async function init() {
      // 1. Load user settings from backend (preferences + lastReadAt)
      try {
        const res = await fetch(`${API_BASE}/api/settings/notifications`);
        if (res.ok) {
          const data = await res.json() as {
            notificationsLastReadAt?: string;
            notifications?: Record<string, boolean>;
          };
          // Take the later of localStorage and Cosmos (multi-device safety)
          const apiLastRead = new Date(data.notificationsLastReadAt ?? 0);
          if (apiLastRead > lastReadAtRef.current) {
            setLastReadAt(apiLastRead);
            lastReadAtRef.current = apiLastRead;
            localStorage.setItem(LAST_READ_KEY, apiLastRead.toISOString());
          }
          // Merge backend prefs into local context
          if (data.notifications) {
            Object.entries(data.notifications).forEach(([key, val]) => {
              updateNotification(key as keyof typeof settings.notifications, val);
            });
          }
        }
      } catch {
        // Backend unavailable — use localStorage value (already set)
      }
      if (cancelled) return;

      // 2. Load notification history
      try {
        const res = await fetch(`${API_BASE}/api/notifications?limit=100`);
        if (res.ok) {
          const data: AppNotification[] = await res.json();
          if (!cancelled) setRaw(data.filter(n => isEnabled(n.sourceEventType)));
        }
      } catch {
        // Backend unavailable — show empty list
      }
      if (cancelled) return;

      // 3. Subscribe to live SSE stream
      es = new EventSource(`${API_BASE}/api/notifications/stream`);
      if (cancelled) { es.close(); es = null; return; }   // unmounted while we were awaiting
      es.onmessage = (e) => {
        const n: AppNotification = JSON.parse(e.data as string);
        if (isEnabled(n.sourceEventType)) {
          setRaw(prev => [n, ...prev].slice(0, 100));
        }
      };
    }

    void init();
    return () => { cancelled = true; es?.close(); es = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const markAllRead = useCallback(async () => {
    // Optimistic update — instant badge clear, no waiting for API
    const now = new Date();
    setLastReadAt(now);
    lastReadAtRef.current = now;
    localStorage.setItem(LAST_READ_KEY, now.toISOString());

    // Sync to Cosmos in background
    try {
      await fetch(`${API_BASE}/api/notifications/mark-all-read`, { method: 'PATCH' });
    } catch {
      // Non-critical — localStorage already updated
    }
  }, []);

  const notifications: Notification[] = raw.map(n => toPanel(n, lastReadAt));
  const unreadCount = notifications.filter(n => !n.read).length;

  return {
    notifications,
    panelNotifications: notifications.slice(0, PANEL_LIMIT),
    unreadCount,
    markAllRead,
  };
}
